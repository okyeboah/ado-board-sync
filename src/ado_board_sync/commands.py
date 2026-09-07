"""Command implementations: gen-csv, import, resync, resync-tasks, close-children,
dedup, audit, sprints.

Each command is parameterised by the project ``Config``. Mutating commands are
dry-run by default and only apply changes when ``args.go`` is set.
"""
from concurrent.futures import ThreadPoolExecutor

from . import csvio, htmlfmt, parser


# Write fan-out width for an apply phase. The phase's jobs always target
# independent work items (distinct ids), so plan order governs reporting only,
# never correctness; each worker thread reuses its own keep-alive connection,
# and 429 throttling is already handled per-request by the client's retry policy.
APPLY_WORKERS = 8


def _apply(jobs):
    """Run each zero-argument job concurrently, returning results in job order."""
    if not jobs:
        return []
    with ThreadPoolExecutor(max_workers=min(APPLY_WORKERS, len(jobs))) as pool:
        return list(pool.map(lambda job: job(), jobs))


# --------------------------------------------------------------------------- #
# gen-csv: backlog Markdown -> import CSV
# --------------------------------------------------------------------------- #
def gen_csv(cfg, args=None, items=None):
    items = items if items is not None else parser.parse_board(cfg)
    rows = csvio.rows_from_board(items, cfg)
    csvio.write_rows(rows, cfg.csv_file)
    n_epics = sum(1 for r in rows if r["Title 1"])
    print(f"Wrote {len(rows)} rows ({n_epics} epics) -> {cfg.csv_file}")
    return 0


def sync_one(cfg, client, args):
    """Create or update exactly one backlog Issue and set its fixed sprint path."""
    code = args.code.upper()
    if not cfg.issue_code_re.fullmatch(code):
        print(f"FAIL {code}: code must match {cfg.code_prefix}-<digits>")
        return 1
    if args.sprint not in {item["name"] for item in cfg.iterations}:
        print(f"FAIL {code}: sprint '{args.sprint}' is not in board.config.json")
        return 1
    current_epic = None
    issue = None
    for item in parser.parse_board(cfg):
        if item["level"] == "epic":
            current_epic = item
        elif item["code"] == code:
            issue = item
            break
    if issue is None or current_epic is None:
        print(f"FAIL {code}: local backlog Issue was not found")
        return 1

    # Scoped to the Issue type. A Task may cite another ticket's code in its own title —
    # "Rate-limit rejections are logged and surfaced to monitoring (PROJ-803)" is a PROJ-805
    # task — and an unscoped CONTAINS matched it, so the cited ticket became unsyncable.
    found = client.wiql(
        f"[System.TeamProject]='{cfg.project}' AND [System.WorkItemType]='{cfg.types['story']}' "
        f"AND [System.Title] CONTAINS '{code}'")
    if len(found) > 1:
        print(f"FAIL {code}: multiple board items match {found}")
        return 1

    description = htmlfmt.markdown_to_html(issue["desc_lines"])
    iteration = f"{cfg.project}\\{args.sprint}"
    operations = [
        {"op": "add", "path": "/fields/System.Title", "value": issue["title"]},
        {"op": "add", "path": "/fields/System.Description", "value": description},
        {"op": "add", "path": "/fields/System.IterationPath", "value": iteration},
    ]
    if found:
        item_id = found[0]
        print(f"update only {code} #{item_id} -> {args.sprint}")
        if not args.go:
            return 0
        status, result = client.patch(item_id, operations)
        if status != 200:
            print(f"FAIL {code}: update returned {status} {result}")
            return 1
    else:
        epic_ids = client.wiql(
            f"[System.TeamProject]='{cfg.project}' AND [System.WorkItemType]='{cfg.types['epic']}' "
            f"AND [System.Title]='{current_epic['title']}'")
        if len(epic_ids) != 1:
            print(f"FAIL {code}: expected one parent Epic, found {epic_ids}")
            return 1
        operations.append({"op": "add", "path": "/relations/-", "value": client.parent_link(epic_ids[0])})
        print(f"create only {code} under Epic #{epic_ids[0]} -> {args.sprint}")
        if not args.go:
            return 0
        status, result = client.create(cfg.types["story"], operations)
        if status not in (200, 201):
            print(f"FAIL {code}: create returned {status} {result}")
            return 1
        item_id = result["id"]

    status, result = client.get_item(item_id)
    fields = result.get("fields", {}) if status == 200 else {}
    if not _iteration_matches(fields.get("System.IterationPath"), iteration):
        print(f"FAIL {code}: read-back iteration does not match {iteration}")
        return 1
    print(f"verified only {code} #{item_id} -> {fields['System.IterationPath']}")
    return 0


# --------------------------------------------------------------------------- #
# import: create Epics/Issues missing from the board (idempotent)
# --------------------------------------------------------------------------- #
def _create(client, wtype, title, desc, parent=None):
    ops = [{"op": "add", "path": "/fields/System.Title", "value": title}]
    if desc:
        ops.append({"op": "add", "path": "/fields/System.Description", "value": desc})
    if parent:
        ops.append({"op": "add", "path": "/relations/-", "value": client.parent_link(parent)})
    st, r = client.create(wtype, ops)
    if st in (200, 201):
        return r.get("id")
    print(f"  FAIL create {wtype} '{title}': {st} {r}")
    return None


def import_items(cfg, client, args):
    rows = csvio.read_rows(cfg.csv_file)
    estype, sttype = cfg.types["epic"], cfg.types["story"]

    ids = client.wiql(f"[System.TeamProject]='{cfg.project}'")
    existing_items = client.get_items(
        ids, ["System.Id", "System.Title", "System.WorkItemType"]
    )
    epic_ids = {}           # epic title (lower) -> id
    epic_title_ids = {}     # epic title (lower) -> [ids]  (duplicate detection)
    existing = {}           # issue code / title (lower) -> id
    existing_code_ids = {}  # issue code -> [ids]          (duplicate detection)
    for w in existing_items:
        f = w["fields"]
        title = f.get("System.Title", "").strip()
        wt = f.get("System.WorkItemType", "")
        if wt == estype:
            epic_ids[title.lower()] = w["id"]
            epic_title_ids.setdefault(title.lower(), []).append(w["id"])
        elif wt == sttype:
            m = cfg.issue_code_re.search(title)
            if m:
                existing[m.group(1).upper()] = w["id"]
                existing_code_ids.setdefault(m.group(1).upper(), []).append(w["id"])
            existing[title.lower()] = w["id"]
        # Other types (e.g. child Tasks) are ignored here: a Task title often
        # carries its parent Issue's code, and classifying it as an Issue would
        # collide with the parent and raise a false duplicate warning below.

    # Surface any duplicates the board already carries. import never creates a
    # second copy of a code that exists, so it will not compound these — but the
    # board is not unique until `dedup --go` clears them, so say so loudly.
    board_dups = {c: ids for c, ids in existing_code_ids.items() if len(ids) > 1}
    board_dups.update({t: ids for t, ids in epic_title_ids.items() if len(ids) > 1})
    if board_dups:
        print("WARNING: the board already contains duplicate work items "
              "(same Issue code or Epic title on more than one item):")
        for key, ids in sorted(board_dups.items()):
            print(f"    {key}: {sorted(ids)}")
        print("    Run `dedup --go` to remove the extras before relying on this board.")

    plan = []  # (kind, title, desc, parent_getter)
    planned = set()  # ('epic'|'issue', key) already queued this run — guards
                     # against duplicate backlog rows creating duplicate items
    last_epic_key = None
    for row in rows:
        wt = row["Work Item Type"]
        t1, t2 = row["Title 1"].strip(), row["Title 2"].strip()
        desc = row["Description"]

        if wt == estype:
            key = t1.lower()
            eid = epic_ids.get(key)
            if not eid:
                for et, ei in epic_ids.items():
                    if key in et or et in key:
                        eid = ei
                        break
            if eid:
                last_epic_key = ("id", eid)
            else:
                last_epic_key = ("epic", t1)  # to be created
                if ("epic", key) not in planned:
                    planned.add(("epic", key))
                    plan.append(("epic", t1, desc, None))
        elif wt == sttype:
            m = cfg.issue_code_re.search(t2)
            code = m.group(1).upper() if m else None
            if (code and code in existing) or t2.lower() in existing:
                continue
            key = ("issue", code or t2.lower())
            if key in planned:
                continue  # same Issue code/title already queued this run
            planned.add(key)
            plan.append(("issue", t2, desc, last_epic_key))

    n_epic = sum(1 for p in plan if p[0] == "epic")
    n_issue = sum(1 for p in plan if p[0] == "issue")
    print(f"Missing items to create: {n_epic} epic(s), {n_issue} issue(s)")
    for kind, title, _desc, _parent in plan:
        print(f"  + {kind}: {title}")

    if not args.go:
        print("\n(dry-run; pass --go to create)")
        return 0

    print("\nCreating...")
    created_epic_ids = {}  # epic title -> new id
    created = 0
    for kind, title, desc, parent in plan:
        if kind == "epic":
            eid = _create(client, cfg.types["epic"], title, desc)
            if eid:
                created_epic_ids[title] = eid
                created += 1
                print(f"  Epic #{eid}: {title}")
        else:
            parent_id = None
            if parent:
                ptype, pval = parent
                parent_id = pval if ptype == "id" else created_epic_ids.get(pval)
            iid = _create(client, cfg.types["story"], title, desc, parent=parent_id)
            if iid:
                created += 1
                print(f"  Issue #{iid}: {title} (parent {parent_id})")

    print(f"\nDone. Created {created} item(s).")
    return 0


# --------------------------------------------------------------------------- #
# resync: update Epic/Issue titles + descriptions to match the backlog
# --------------------------------------------------------------------------- #
def resync(cfg, client, args):
    want_epics, want_issues = csvio.backlog_maps(cfg)
    estype, sttype = cfg.types["epic"], cfg.types["story"]

    ids = client.wiql(
        f"[System.TeamProject]='{cfg.project}' "
        f"AND [System.WorkItemType] IN ('{estype}','{sttype}')"
    )
    board = client.get_items(
        ids,
        ["System.Id", "System.Title", "System.Description", "System.WorkItemType"],
    )

    updates = []  # (id, title, [ops])
    for w in board:
        f = w["fields"]
        wt = f["System.WorkItemType"]
        title = f.get("System.Title", "").strip()
        desc = f.get("System.Description", "")

        if wt == estype:
            matched = None
            for ctitle, cdesc in want_epics.items():
                if ctitle in title.lower() or title.lower() in ctitle:
                    matched = cdesc
                    break
            if matched and htmlfmt.norm(matched) != htmlfmt.norm(desc):
                updates.append((w["id"], title, [
                    {"op": "add", "path": "/fields/System.Description", "value": matched},
                ]))
        elif wt == sttype:
            m = cfg.issue_code_re.search(title)
            if not m:
                continue
            ci = want_issues.get(m.group(1).upper())
            if not ci:
                continue
            ops = []
            if title != ci["title"]:
                ops.append({"op": "add", "path": "/fields/System.Title", "value": ci["title"]})
            if htmlfmt.norm(desc) != htmlfmt.norm(ci["desc"]):
                ops.append({"op": "add", "path": "/fields/System.Description", "value": ci["desc"]})
            if ops:
                updates.append((w["id"], title, ops))

    print(f"Items needing title/description resync: {len(updates)}")
    for wid, title, ops in updates:
        print(f"  #{wid} {title}  ({', '.join(o['path'].split('.')[-1] for o in ops)})")

    if not args.go:
        print("\n(dry-run; pass --go to apply)")
        return 0

    print("\nApplying...")
    jobs = [lambda wid=wid, ops=ops: client.patch(wid, ops) for wid, _title, ops in updates]
    for (wid, _title, _ops), (st, _r) in zip(updates, _apply(jobs)):
        print(f"  #{wid} -> {'OK' if st == 200 else f'FAIL {st}'}")
    print("\nResync finished.")
    return 0


# --------------------------------------------------------------------------- #
# resync-tasks: reconcile each Issue's child Tasks to the backlog bullets
# --------------------------------------------------------------------------- #
def _issue_map(cfg, client, extra_field=None, code=None):
    """CODE -> issue_id, or CODE -> (issue_id, extra_field value) if ``extra_field`` is given.

    ``code``, if given, scopes the query to Issues whose title contains it, instead of
    reading every Issue on the board just to look up one.
    """
    fields = ["System.Title"] + ([extra_field] if extra_field else [])
    wiql = (
        f"[System.TeamProject]='{cfg.project}' "
        f"AND [System.WorkItemType]='{cfg.types['story']}'"
    )
    if code:
        wiql += f" AND [System.Title] CONTAINS '{code}'"
    ids = client.wiql(wiql)
    out = {}
    for w in client.get_items(ids, fields):
        m = cfg.issue_code_re.search(w["fields"]["System.Title"])
        if m:
            found = m.group(1).upper()
            # Lowest id wins on a duplicated code. ``dedup`` keeps min(ids), so the
            # lowest is the item that survives a clean-up; assigning the other one
            # writes to a work item that is about to be deleted. Compared explicitly
            # rather than by overwriting: the WIQL orders by nothing, so the previous
            # last-write-wins took whichever id the server happened to return last.
            prior = out.get(found)
            prior_id = (prior[0] if extra_field else prior) if prior is not None else None
            if prior_id is None or w["id"] < prior_id:
                out[found] = (w["id"], w["fields"].get(extra_field)) if extra_field else w["id"]
    return out


def _child_tasks(cfg, client, parent_id, extra_field=None):
    """{task_id: title}, or [(task_id, extra_field value)] if ``extra_field`` is given.

    One relations-expand of the single parent -- cheap for a one-Issue scope, but O(N) round
    trips if called once per Issue for N Issues. Use ``_tasks_by_parent`` instead when reconciling
    more than one Issue at a time.
    """
    _, r = client.get_item(parent_id, expand="relations")
    child_ids = [
        int(rel["url"].split("/")[-1])
        for rel in r.get("relations", [])
        if rel.get("rel") == "System.LinkTypes.Hierarchy-Forward"
    ]
    fields = ["System.WorkItemType"] + [extra_field or "System.Title"]
    out = {} if not extra_field else []
    if child_ids:
        for w in client.get_items(child_ids, fields):
            if w["fields"].get("System.WorkItemType") == cfg.types["task"]:
                if extra_field:
                    out.append((w["id"], w["fields"].get(extra_field)))
                else:
                    out[w["id"]] = w["fields"]["System.Title"]
    return out


def _tasks_by_parent(cfg, client, parent_ids, extra_field=None):
    """Every child Task of the given parent Issue ids, in one WIQL query plus one batched
    get_items call -- versus a relations-expand per Issue (see ``_child_tasks``), which turns a
    reconcile of N Issues into N extra round trips. That per-Issue pattern was the main reason
    resync-tasks/sprints/assign felt slow on boards with many Issues even on a healthy network.

    Returns {parent_id: {task_id: title}}, or {parent_id: [(task_id, extra_field value)]} if
    ``extra_field`` is given -- always one entry per requested parent, even with no Tasks, so a
    caller indexing by any id from its own ``parent_ids`` can do so directly (no ``.get(id, ...)``
    fallback needed).
    """
    parent_ids = set(parent_ids)
    out = {pid: ([] if extra_field else {}) for pid in parent_ids}
    if not parent_ids:
        return out
    ids = client.wiql(
        f"[System.TeamProject]='{cfg.project}' AND [System.WorkItemType]='{cfg.types['task']}'"
    )
    fields = ["System.Parent"] + [extra_field or "System.Title"]
    items = client.get_items(ids, fields)
    if extra_field:
        for w in items:
            pid = w["fields"].get("System.Parent")
            if pid in parent_ids:
                out[pid].append((w["id"], w["fields"].get(extra_field)))
    else:
        for w in items:
            pid = w["fields"].get("System.Parent")
            if pid in parent_ids:
                out[pid][w["id"]] = w["fields"]["System.Title"]
    return out


def resync_tasks(cfg, client, args):
    bullets = parser.tasks_by_code(cfg)
    # Optional single-Issue scope. Without it the command reconciles the whole board, so
    # syncing one ticket's Tasks also applied every other ticket's pending bullets — which
    # is more than the caller asked for, and pushed people towards one-off scripts instead.
    only = (getattr(args, "code", None) or "").upper()
    if only:
        if only not in bullets:
            print(f"FAIL {only}: no Task bullets for that code in the local backlog")
            return 1
        bullets = {only: bullets[only]}
    imap = _issue_map(cfg, client, code=only or None)
    tmax = cfg.task_title_max

    # Scoped to one Issue, _child_tasks' single relations-expand is already minimal. Reconciling
    # the whole board instead, batch every Issue's Tasks in one query up front rather than paying
    # a relations-expand per Issue.
    if only:
        existing_by_parent = {pid: _child_tasks(cfg, client, pid) for pid in imap.values()}
    else:
        existing_by_parent = _tasks_by_parent(cfg, client, imap.values())

    plan = []  # (code, parent_id, [add_bullets], [(del_id, del_title)])
    for code, bs in bullets.items():
        pid = imap.get(code)
        if not pid:
            continue
        desired = {htmlfmt.plain(b)[:tmax]: b for b in bs}
        existing = existing_by_parent[pid]
        existing_titles = set(existing.values())
        add = [orig for t, orig in desired.items() if t not in existing_titles]
        dele = [(i, t) for i, t in existing.items() if t not in desired]
        if add or dele:
            plan.append((code, pid, add, dele))

    tot_add = sum(len(a) for _, _, a, _ in plan)
    tot_del = sum(len(d) for _, _, _, d in plan)
    print(f"Issues needing task changes: {len(plan)}  (+{tot_add} add, -{tot_del} delete)")
    for code, pid, add, dele in plan:
        print(f"  {code} (#{pid}): +{len(add)} -{len(dele)}")
        for b in add:
            print(f"      + {htmlfmt.plain(b)[:80]}")
        for i, t in dele:
            print(f"      - #{i} {t[:80]}")

    if not args.go:
        print("\n(dry-run; pass --go to apply)")
        return 0

    # Flatten the plan into independent create/delete jobs (one work item each) and
    # write them concurrently; the flattened order is preserved for reporting.
    actions = []  # ("add", code, pid, bullet) | ("del", code, task_id, title)
    for code, pid, add, dele in plan:
        actions += [("add", code, pid, b) for b in add]
        actions += [("del", code, i, t) for i, t in dele]

    def _do(action):
        kind, code, target, value = action
        if kind == "add":
            ops = [
                {"op": "add", "path": "/fields/System.Title", "value": htmlfmt.plain(value)[:tmax]},
                {"op": "add", "path": "/fields/System.Description", "value": htmlfmt.inline(value)},
                {"op": "add", "path": "/relations/-", "value": client.parent_link(target)},
            ]
            return client.create(cfg.types["task"], ops)
        return client.delete(target)

    print("\nApplying...")
    for action, (st, r) in zip(actions, _apply([lambda a=a: _do(a) for a in actions])):
        kind, code, target = action[0], action[1], action[2]
        if kind == "add":
            print(f"  {code} + {'OK' if st in (200, 201) else f'FAIL {st} {r}'}")
        else:
            print(f"  {code} - #{target} {'OK' if st in (200, 204) else f'FAIL {st}'}")
    print(f"\nDONE: +{tot_add} tasks, -{tot_del} tasks")
    return 0


# --------------------------------------------------------------------------- #
# close-children: cascade a Done Issue's completion onto its child Tasks
# --------------------------------------------------------------------------- #
# Azure DevOps propagates state upward (a parent can auto-close when its
# children close) but never downward: marking an Issue Done leaves its Tasks
# open. This reconcile command closes those orphaned Tasks. It is deliberately
# NOT part of `sync` — `sync` is a structural reconcile, whereas this changes
# workflow status, so it must be invoked explicitly.
def _assignee_value(field):
    """The string form of ``System.AssignedTo`` for a PATCH, or ``None`` if
    unassigned. Reads come back as an identity object; writes take the
    ``uniqueName`` string."""
    if not field:
        return None
    if isinstance(field, dict):
        return field.get("uniqueName") or field.get("id")
    return field


def _hierarchy(cfg, client):
    """Every Epic/Issue/Task on the board, with its title, description, state,
    parent, and assignee -- in one WIQL query plus one batched get_items call.

    Descriptions come along so callers that compare them against the backlog
    (``audit``) reuse this same single pair of round trips instead of issuing a
    second full-board read beside ``close-children``'s hierarchy walk.
    """
    types = "','".join([cfg.types["epic"], cfg.types["story"], cfg.types["task"]])
    ids = client.wiql(
        f"[System.TeamProject]='{cfg.project}' "
        f"AND [System.WorkItemType] IN ('{types}')"
    )
    fields = [
        "System.Id", "System.Title", "System.Description", "System.State",
        "System.WorkItemType", "System.Parent", "System.AssignedTo",
    ]
    rec = {}
    for w in client.get_items(ids, fields):
        f = w["fields"]
        rec[w["id"]] = {
            "title": f.get("System.Title", ""),
            "desc": f.get("System.Description", ""),
            "state": f.get("System.State", ""),
            "type": f["System.WorkItemType"],
            "parent": f.get("System.Parent"),
            "assignee": f.get("System.AssignedTo"),
        }
    kids = {}
    for wid, r in rec.items():
        if r["parent"] in rec:
            kids.setdefault(r["parent"], []).append(wid)
    return rec, kids


def _drift(rec, kids, done):
    """The two ways recorded states can disagree with the hierarchy (see ``state_drift``)."""
    downward = {}
    for wid, r in rec.items():
        if r["state"] == done:
            continue
        ancestor = _nearest_done_ancestor(rec, wid, done)
        if ancestor is not None:
            downward[wid] = ancestor

    upward = [
        wid for wid, children in kids.items()
        if rec[wid]["state"] != done and all(rec[c]["state"] == done for c in children)
    ]
    return downward, upward


def state_drift(cfg, client):
    """Return the two ways a board's states can disagree with its hierarchy.

    ``downward`` maps each open item to the nearest ancestor already done —
    Azure DevOps never cascades state, so closing an Epic silently leaves its
    Issues and Tasks open. ``upward`` lists parents whose children are all done
    while the parent is not; that one is a judgement call, not a defect, because
    a parent can hold review or sign-off work of its own.
    """
    done = cfg.states["done"]
    rec, kids = _hierarchy(cfg, client)
    downward, upward = _drift(rec, kids, done)
    return rec, kids, downward, upward


def _nearest_done_ancestor(rec, wid, done):
    """The closest ancestor already marked done, or None."""
    parent = rec[wid]["parent"]
    while parent in rec:
        if rec[parent]["state"] == done:
            return parent
        parent = rec[parent]["parent"]
    return None


def _group_by_ancestor(downward):
    """Group each open item's id under the done ancestor that explains why it closes."""
    by_ancestor = {}
    for wid, ancestor in downward.items():
        by_ancestor.setdefault(ancestor, []).append(wid)
    return by_ancestor


def close_children(cfg, client, args):
    done = cfg.states["done"]
    assign_from_parent = getattr(args, "assign_from_parent", False)
    rec, _kids, downward, _upward = state_drift(cfg, client)

    plan = {}
    for ancestor, wids in _group_by_ancestor(downward).items():
        parent_assignee = (
            _assignee_value(rec[ancestor]["assignee"]) if assign_from_parent else None
        )
        for wid in wids:
            # Only fill a currently-unassigned item; never overwrite a deliberate one.
            assignee = parent_assignee if (parent_assignee and not rec[wid]["assignee"]) else None
            plan.setdefault(ancestor, []).append((wid, rec[wid]["title"], assignee))

    ordered = {pid: sorted(plan[pid]) for pid in sorted(plan)}

    total = sum(len(v) for v in ordered.values())
    n_assign = sum(1 for v in ordered.values() for *_, a in v if a)
    head = f"Done parents with open descendants: {len(ordered)}  ({total} item(s) to close"
    if assign_from_parent:
        head += f", {n_assign} to assign"
    print(head + ")")
    for pid, items in ordered.items():
        print(f"  {rec[pid]['type']} #{pid}: closing {len(items)} item(s)")
        for wid, title, assignee in items:
            tag = f"  [+assign {assignee}]" if assignee else ""
            print(f"      -> {rec[wid]['type']} #{wid} {title[:72]}{tag}")

    if not args.go:
        print("\n(dry-run; pass --go to apply)")
        return 0

    print("\nApplying...")
    jobs = [
        lambda wid=wid, assignee=assignee: client.patch(wid, [
            {"op": "add", "path": "/fields/System.State", "value": done},
            *([{"op": "add", "path": "/fields/System.AssignedTo", "value": assignee}]
              if assignee else []),
        ])
        for items in ordered.values() for wid, _title, assignee in items
    ]
    flat = [(wid, assignee) for items in ordered.values() for wid, _t, assignee in items]
    for (wid, _a), (st, r) in zip(flat, _apply(jobs)):
        print(f"  #{wid} -> {done}: {'OK' if st == 200 else f'FAIL {st} {r}'}")
    print(f"\nDONE: closed {total} item(s)")
    return 0


# --------------------------------------------------------------------------- #
# set-state: move explicit work items to a state, by id
# --------------------------------------------------------------------------- #
# Some board repairs fall outside any backlog reconcile: a Task whose bullet has
# left the backlog, a straggler blocking close-children, an item another team
# moved while a cascade ran. This sets System.State -- and ticks a leading "[ ]"
# title checkbox when the target is the terminal state, so a done Task's visible
# wording agrees with its state -- for exactly the ids the caller names. It never
# derives ids from the backlog; reaching for it is always an explicit decision.
def set_state(cfg, client, args):
    target = args.state or cfg.states["done"]
    tick = not getattr(args, "no_tick", False) and target == cfg.states["done"]

    wanted = sorted(set(args.ids))
    fields = {
        w["id"]: w["fields"]
        for w in client.get_items(wanted, ["System.Id", "System.Title", "System.State"])
    }
    missing = [wid for wid in wanted if wid not in fields]
    if missing:
        print(f"WARN not found on this board: {missing}")

    plan = []  # (id, title_from, ops)
    for wid in wanted:
        f = fields.get(wid)
        if f is None:
            continue
        title = f.get("System.Title", "")
        ops = []
        if f.get("System.State") != target:
            ops.append({"op": "add", "path": "/fields/System.State", "value": target})
        if tick and title.startswith("[ ]"):
            ops.append({
                "op": "add", "path": "/fields/System.Title",
                "value": "[x]" + title[len("[ ]"):],
            })
        plan.append((wid, title, ops))

    unchanged = len(wanted) - len(plan) - len(missing)
    head = f"State changes to '{target}': {len(plan)} item(s)"
    if unchanged:
        head += f"; {unchanged} already there"
    print(head)
    for wid, title, _ops in plan:
        note = " (+tick)" if any(o["path"] == "/fields/System.Title" for o in _ops) else ""
        print(f"  #{wid} {title[:70]}{note}")

    if not args.go:
        if missing:
            return 1
        print("\n(dry-run; pass --go to apply)")
        return 0

    jobs = [lambda wid=wid, ops=ops: client.patch(wid, ops) for wid, _t, ops in plan]
    ok = fail = 0
    for (wid, _t, _o), (st, r) in zip(plan, _apply(jobs)):
        if st == 200:
            ok += 1
        else:
            fail += 1
            print(f"  #{wid} -> FAIL {st} {r}")
    print(f"\nDONE: set '{target}' on {ok} item(s)")
    return 0 if (fail == 0 and not missing) else 1


# --------------------------------------------------------------------------- #
# sprints: create iteration nodes and assign Issues (+ child Tasks) to them
# --------------------------------------------------------------------------- #
def _iteration_plan(cfg):
    """Flatten cfg.iterations into an ordered plan and a code->sprint map.

    Each configured iteration lists the Issue codes it owns. If a code appears
    in more than one iteration the *earliest listed* wins (matches a
    schedule where an item starts in its first sprint).
    """
    plan = []          # [(name, start, finish, [codes])]
    code_sprint = {}   # CODE -> sprint name
    for it in cfg.iterations:
        name = it["name"]
        codes = [c.upper() for c in it.get("items", [])]
        plan.append((name, it.get("start"), it.get("finish"), codes))
        for c in codes:
            code_sprint.setdefault(c, name)
    return plan, code_sprint


def _iteration_matches(current, desired):
    """True if a work item's current System.IterationPath already equals ``desired``.
    Unlike AssignedTo, IterationPath comes back as a plain string, not an identity object."""
    if not current:
        return False
    return str(current).strip().lower() == desired.strip().lower()


def sprints(cfg, client, args):
    if not cfg.iterations:
        print("No iterations configured. Add an 'iterations' array to board.config.json "
              "(each entry: {name, start?, finish?, items:[codes]}).")
        return 1

    plan, code_sprint = _iteration_plan(cfg)
    code_item = _issue_map(cfg, client, "System.IterationPath")   # read-only; safe in dry-run
    board_codes = set(code_item)
    assigned_on_board = {c for c in code_sprint if c in board_codes}
    unknown = sorted(set(code_sprint) - board_codes)   # in config, not on board
    uncovered = sorted(board_codes - set(code_sprint))  # on board, no sprint

    print("=" * 60)
    print(f"SPRINT PLAN — {cfg.org}/{cfg.project}")
    print("=" * 60)
    for name, start, finish, codes in plan:
        span = f"{start} -> {finish}" if (start or finish) else "(no dates)"
        present = [c for c in codes if c in board_codes]
        print(f"\n{name}  {span}")
        print(f"  {len(present)} issue(s): {', '.join(present) if present else '(none)'}")
    print("\n" + "-" * 60)
    print(f"Assigned issues: {len(assigned_on_board)} / {len(board_codes)} on board")
    if unknown:
        print(f"WARN codes in config not found on board: {unknown}")
    if uncovered:
        print(f"WARN board issues with no sprint (left unassigned): {uncovered}")
    if not unknown and not uncovered:
        print("Coverage OK: every board Issue maps to exactly one sprint.")

    assign_only = getattr(args, "assign_only", False)
    do_tasks = not getattr(args, "no_tasks", False)

    # One query for every assigned Issue's Tasks, instead of a relations-expand per Issue.
    tasks_by_parent = {}
    if do_tasks:
        parent_ids = [code_item[code][0] for code in assigned_on_board]
        tasks_by_parent = _tasks_by_parent(cfg, client, parent_ids, "System.IterationPath")

    # (code, issue_id, desired_path, issue_needs, [task_ids])
    diff_plan = []
    skipped = 0
    for code in sorted(assigned_on_board):
        pid, current_path = code_item[code]
        name = code_sprint[code]
        desired_path = f"{cfg.project}\\{name}"

        if _iteration_matches(current_path, desired_path):
            issue_needs = False
            skipped += 1
        else:
            issue_needs = True

        task_ids = []
        if do_tasks:
            for tid, tpath in tasks_by_parent[pid]:
                if _iteration_matches(tpath, desired_path):
                    continue
                task_ids.append(tid)

        if issue_needs or task_ids:
            diff_plan.append((code, pid, desired_path, issue_needs, task_ids))

    n_issue = sum(1 for *_, needs, _ in diff_plan if needs)
    n_task = sum(len(t) for *_, t in diff_plan)
    print(f"\nIteration changes: {n_issue} issue(s), {n_task} task(s)"
          + (f"; {skipped} already correct" if skipped else ""))
    for code, pid, desired_path, issue_needs, task_ids in diff_plan:
        if issue_needs:
            tag = f"  [+{len(task_ids)} task(s)]" if task_ids else ""
            print(f"  {code} (#{pid}) -> {desired_path}{tag}")
        else:
            print(f"  {code} (#{pid}): issue kept; {len(task_ids)} task(s) -> {desired_path}")

    if not args.go:
        print("\n(dry-run; pass --go to create sprints and assign work items)")
        return 0

    if not assign_only:
        print("\nCreating / updating iteration nodes...")
        idents = {}
        for name, start, finish, _ in plan:
            ok, ident, note = client.ensure_iteration(name, start, finish)
            print(f"  {name}: {note if ok else 'FAIL ' + note}")
            if ok and ident:
                idents[name] = ident
        team = cfg.team or client.default_team()
        if team and idents:
            print(f"\nAdding sprints to team '{team}'...")
            for name, ident in idents.items():
                ok, note = client.add_team_iteration(team, ident)
                print(f"  {name}: {note if ok else 'FAIL ' + note}")
        elif not team:
            print("\n(no team resolved; sprints created but not added to a team's sprint view)")

    # One independent patch per work item, written concurrently. The rare
    # reset-on-missing recovery stays on this thread: it runs only after a patch
    # has failed, keeping each item's failure and its recovery in order.
    def _reset_iteration(wid, label, indent):
        reset_op = [{"op": "add", "path": "/fields/System.IterationPath", "value": cfg.project}]
        st_reset, r_reset = client.patch(wid, reset_op)
        if st_reset == 200:
            print(f"{indent}Successfully reset {label} to {cfg.project}")
        else:
            print(f"{indent}FAIL {label} reset: {st_reset} {r_reset}")

    print("\nApplying...")
    writes = []  # ((kind, code, wid, desired_path), thunk), flattened in plan order
    for code, pid, desired_path, issue_needs, task_ids in diff_plan:
        op = [{"op": "add", "path": "/fields/System.IterationPath", "value": desired_path}]
        if issue_needs:
            writes.append((("issue", code, pid, desired_path),
                           lambda wid=pid, op=op: client.patch(wid, op)))
        for tid in task_ids:
            writes.append((("task", code, tid, desired_path),
                           lambda wid=tid, op=op: client.patch(wid, op)))

    ok = fail = task_ok = task_fail = 0
    reset_on_missing = getattr(args, "reset_on_missing", False)
    results = _apply([thunk for _meta, thunk in writes])
    for (kind, code, wid, desired_path), (st, r) in zip([m for m, _ in writes], results):
        if st == 200:
            if kind == "issue":
                ok += 1
            else:
                task_ok += 1
            continue
        if kind == "issue":
            fail += 1
            print(f"  FAIL issue {code} #{wid} -> {desired_path}: {st} {r}")
            if reset_on_missing:
                print(f"  Resetting issue {code} #{wid} to project root iteration...")
                _reset_iteration(wid, f"issue {code} #{wid}", "    ")
        else:
            task_fail += 1
            print(f"  FAIL task #{wid} of {code} -> {desired_path}: {st} {r}")
            if reset_on_missing:
                _reset_iteration(wid, f"task #{wid}", "      ")

    print("\n" + "-" * 60)
    print(f"Issues assigned: {ok} ok, {fail} fail")
    if do_tasks:
        print(f"Tasks  assigned: {task_ok} ok, {task_fail} fail")
    return 0 if (fail == 0 and task_fail == 0) else 1


# --------------------------------------------------------------------------- #
# assign: set System.AssignedTo on Issues (+ child Tasks) from the config
# --------------------------------------------------------------------------- #
# Azure DevOps has no backlog-driven ownership: assignment is a per-item field
# set by hand in the UI. This command drives it from the `assignees` config
# ({identity: [codes]}) so a planned work split is reproducible and reviewable.
# Like `sprints`/`close-children` it changes ownership metadata, not structure,
# so it is invoked explicitly and never folded into `sync`.
def _assignee_ids(cfg):
    """Flatten cfg.assignees ({identity: [codes]}) into CODE -> identity.

    If a code is listed under more than one identity the *first listed* wins
    (mirrors `sprints`, where the earliest bucket claims a shared code)."""
    code_owner = {}
    for identity, codes in (cfg.assignees or {}).items():
        for c in codes:
            code_owner.setdefault(c.upper(), identity)
    return code_owner


def _assignee_matches(field, desired):
    """True if a work item's current System.AssignedTo already resolves to
    ``desired``. Reads come back as an identity object; compare the wanted
    string against its uniqueName, id, and displayName, case-insensitively."""
    if not field:
        return False
    want = desired.strip().lower()
    if isinstance(field, dict):
        return any(
            (field.get(k) or "").strip().lower() == want
            for k in ("uniqueName", "id", "displayName")
        )
    return str(field).strip().lower() == want


def assign(cfg, client, args):
    owner = _assignee_ids(cfg)
    if not owner:
        print("No assignees configured. Add an 'assignees' object to board.config.json "
              "(each key an ADO identity, each value a list of Issue codes).")
        return 1

    story = cfg.types["story"]
    only_unassigned = getattr(args, "only_unassigned", False)
    do_tasks = not getattr(args, "no_tasks", False)

    ids = client.wiql(
        f"[System.TeamProject]='{cfg.project}' "
        f"AND [System.WorkItemType]='{story}'"
    )
    code_item = {}  # CODE -> (issue_id, current_assignee_field)
    for w in client.get_items(ids, ["System.Id", "System.Title", "System.AssignedTo"]):
        m = cfg.issue_code_re.search(w["fields"].get("System.Title", ""))
        if m:
            # Lowest id wins -- see _issue_map. Same rule, second map.
            found = m.group(1).upper()
            prior = code_item.get(found)
            if prior is None or w["id"] < prior[0]:
                code_item[found] = (w["id"], w["fields"].get("System.AssignedTo"))

    board_codes = set(code_item)
    unknown = sorted(set(owner) - board_codes)     # configured, not on the board
    uncovered = sorted(board_codes - set(owner))   # on the board, no owner in config
    codes_in_scope = sorted(set(owner) & board_codes)

    # One query for every in-scope Issue's Tasks, instead of a relations-expand per Issue.
    tasks_by_parent = {}
    if do_tasks:
        parent_ids = [code_item[code][0] for code in codes_in_scope]
        tasks_by_parent = _tasks_by_parent(cfg, client, parent_ids, "System.AssignedTo")

    # (code, issue_id, desired, current_display, issue_needs, [task_ids])
    plan = []
    skipped = 0
    for code in codes_in_scope:
        iid, current = code_item[code]
        desired = owner[code]

        if _assignee_matches(current, desired) or (only_unassigned and current):
            issue_needs = False
            skipped += 1
        else:
            issue_needs = True

        task_ids = []
        if do_tasks:
            for tid, tfield in tasks_by_parent[iid]:
                if _assignee_matches(tfield, desired) or (only_unassigned and tfield):
                    continue
                task_ids.append(tid)

        if issue_needs or task_ids:
            plan.append((code, iid, desired, _assignee_value(current), issue_needs, task_ids))

    n_issue = sum(1 for *_, needs, _ in plan if needs)
    n_task = sum(len(t) for *_, t in plan)
    print(f"Assignee changes: {n_issue} issue(s), {n_task} task(s)"
          + (f"; {skipped} already correct" if skipped else ""))
    for code, iid, desired, current_disp, issue_needs, task_ids in plan:
        if issue_needs:
            frm = current_disp or "(unassigned)"
            tag = f"  [+{len(task_ids)} task(s)]" if task_ids else ""
            print(f"  {code} (#{iid}): {frm} -> {desired}{tag}")
        else:
            print(f"  {code} (#{iid}): issue kept; {len(task_ids)} task(s) -> {desired}")
    if unknown:
        print(f"WARN codes in config not found on board: {unknown}")
    if uncovered:
        print(f"INFO board issues with no assignee in config: {uncovered}")

    if not args.go:
        print("\n(dry-run; pass --go to apply)")
        return 0

    print("\nApplying...")
    writes = []  # ((kind, code, wid, desired), thunk), flattened in plan order
    for code, iid, desired, _current, issue_needs, task_ids in plan:
        op = [{"op": "add", "path": "/fields/System.AssignedTo", "value": desired}]
        if issue_needs:
            writes.append((("issue", code, iid, desired),
                           lambda wid=iid, op=op: client.patch(wid, op)))
        for tid in task_ids:
            writes.append((("task", code, tid, desired),
                           lambda wid=tid, op=op: client.patch(wid, op)))

    ok = fail = task_ok = task_fail = 0
    results = _apply([thunk for _meta, thunk in writes])
    for (kind, code, wid, desired), (st, r) in zip([m for m, _ in writes], results):
        if kind == "issue":
            if st == 200:
                ok += 1
            else:
                fail += 1
                print(f"  FAIL issue {code} #{wid} -> {desired}: {st} {r}")
        else:
            if st == 200:
                task_ok += 1
            else:
                task_fail += 1
                print(f"  FAIL task #{wid} of {code} -> {desired}: {st} {r}")

    print("\n" + "-" * 60)
    print(f"Issues assigned: {ok} ok, {fail} fail")
    if do_tasks:
        print(f"Tasks  assigned: {task_ok} ok, {task_fail} fail")
    return 0 if (fail == 0 and task_fail == 0) else 1


# --------------------------------------------------------------------------- #
# dedup: delete duplicate work items
# --------------------------------------------------------------------------- #
def dedup(cfg, client, args):
    estype, sttype, tasktype = cfg.types["epic"], cfg.types["story"], cfg.types["task"]
    ids = client.wiql(f"[System.TeamProject]='{cfg.project}'")
    print(f"Found {len(ids)} total work items. Checking for duplicates...")

    # System.Parent rides along on the same batched read as everything else -- one
    # query serves all three levels. Resolving parents through relations instead
    # cost one per-item expand round trip per Task, the N+1 that made dedup crawl
    # on large boards.
    items = client.get_items(
        ids, ["System.Id", "System.Title", "System.WorkItemType", "System.Parent"]
    )
    by_title = {}        # (type, title_lower) -> [ids]   (Epics)
    by_code = {}         # code -> [(id, title)]          (Issues)
    task_by_parent = {}  # parent_id -> [(id, title)]     (Tasks)

    for w in items:
        wt = w["fields"]["System.WorkItemType"]
        title = w["fields"].get("System.Title", "").strip()
        if wt == estype:
            by_title.setdefault((estype, title.lower()), []).append(w["id"])
        elif wt == sttype:
            m = cfg.issue_code_re.search(title)
            if m:
                by_code.setdefault(m.group(1).upper(), []).append((w["id"], title))
            else:
                by_title.setdefault((sttype, title.lower()), []).append(w["id"])
        elif wt == tasktype:
            parent_id = w["fields"].get("System.Parent")
            if parent_id:
                task_by_parent.setdefault(parent_id, []).append((w["id"], title))

    to_delete = []

    dup_epics = {t: w for (typ, t), w in by_title.items() if len(w) > 1}
    if dup_epics:
        print("\nDuplicate Epics:")
        for title, wids in dup_epics.items():
            keep = min(wids)
            dele = [x for x in wids if x != keep]
            to_delete += dele
            print(f"  '{title}': keep #{keep}, delete {dele}")

    dup_issues = {c: items_ for c, items_ in by_code.items() if len(items_) > 1}
    if dup_issues:
        print("\nDuplicate Issues (by code):")
        for code, items_ in dup_issues.items():
            keep = min(items_, key=lambda x: x[0])
            dele = [x[0] for x in items_ if x[0] != keep[0]]
            to_delete += dele
            print(f"  {code}: keep #{keep[0]}, delete {dele}")

    for pid, tasks in task_by_parent.items():
        groups = {}
        for tid, ttitle in tasks:
            groups.setdefault(ttitle.lower(), []).append(tid)
        for ttitle, tids in groups.items():
            if len(tids) > 1:
                keep = min(tids)
                dele = [x for x in tids if x != keep]
                to_delete += dele
                print(f"  Task '{ttitle}' under #{pid}: keep #{keep}, delete {dele}")

    # Cascade: a duplicate Epic/Issue we remove takes its child Tasks with it.
    # A plain work-item DELETE does not remove children, so without this the
    # tasks under a deleted duplicate are left orphaned — and a duplicate
    # parent's tasks are themselves duplicates of the kept parent's tasks.
    for pid in list(to_delete):
        for tid, ttitle in task_by_parent.get(pid, []):
            if tid not in to_delete:
                to_delete.append(tid)
                print(f"  Child Task #{tid} of removed #{pid}: {ttitle} (cascade)")

    if not to_delete:
        print("\nNo duplicates detected.")
        return 0

    print(f"\nTotal items to delete: {len(to_delete)}")
    if not args.go:
        print("(dry-run; pass --go to apply)")
        return 0

    print("\nDeleting...")
    results = _apply([lambda wid=wid: client.delete(wid) for wid in to_delete])
    for wid, (st, _) in zip(to_delete, results):
        print(f"  delete #{wid} -> {'OK' if st in (200, 204) else f'FAIL {st}'}")
    print("\nCleanup finished.")
    return 0


# --------------------------------------------------------------------------- #
# check-html: read-only, offline check that every description converts cleanly
# --------------------------------------------------------------------------- #
def check_html(cfg, args=None, items=None):
    """Verify each generated Epic/Issue/Task description is well-formed HTML.

    Azure DevOps shows the description HTML as stored, so a converter that
    interleaves tags produces a broken card and no other command notices:
    ``audit`` compares board against backlog, and both sides are equally wrong.
    """
    items = items if items is not None else parser.parse_board(cfg)
    checked = bad = 0

    for it in items:
        name = it.get("code") or it["title"]
        blocks = [("description", htmlfmt.markdown_to_html(it["desc_lines"]))]
        blocks += [
            (f"task {htmlfmt.plain(b)[:40]}", htmlfmt.inline(b))
            for b in it.get("bullets", [])
        ]
        for what, markup in blocks:
            checked += 1
            problems = htmlfmt.unbalanced(markup)
            if problems:
                bad += 1
                print(f"  {name} ({what}): {'; '.join(problems[:3])}")

    print(f"Descriptions checked: {checked}, malformed: {bad}")
    return 1 if bad else 0


# --------------------------------------------------------------------------- #
# audit: read-only check that the board matches the backlog (returns exit code)
# --------------------------------------------------------------------------- #
def audit(cfg, client, args=None):
    want_epics, want_issues = csvio.backlog_maps(cfg)
    bullets = parser.tasks_by_code(cfg)
    estype, sttype = cfg.types["epic"], cfg.types["story"]
    tmax = cfg.task_title_max

    # One pair of round trips serves every check below: Epic/Issue identity,
    # title/description parity against the backlog, Task parity per Issue, and
    # state-vs-hierarchy agreement. Reading Epics/Issues separately from the
    # hierarchy walk used to cost two full-board reads where one suffices.
    rec, kids = _hierarchy(cfg, client)
    b_epics, b_issues = {}, {}
    epic_title_ids = {}   # epic title (lower) -> [ids]   (duplicate detection)
    issue_code_ids = {}   # issue code -> [ids]           (duplicate detection)
    for wid, r in rec.items():
        t = r["title"].strip()
        if r["type"] == estype:
            b_epics[wid] = {"title": t, "desc": r["desc"]}
            epic_title_ids.setdefault(t.lower(), []).append(wid)
        elif r["type"] == sttype:
            m = cfg.issue_code_re.search(t)
            if m:
                code = m.group(1).upper()
                b_issues[code] = {
                    "id": wid, "title": t, "desc": r["desc"],
                }
                issue_code_ids.setdefault(code, []).append(wid)
        # A Task whose title cites another ticket's code ("…surfaced to monitoring
        # (PROJ-101)") is neither a duplicate Issue nor its description's twin.
        # Sorting only Epic and Story types into the maps keeps a cited code from
        # inventing both a duplicate finding and a description-drift one — which
        # is what a bare else branch did on any board where tasks cite codes.

    fails = []
    # Duplicate work items sharing an Issue code / Epic title collapse into a
    # single map entry above, so they are otherwise invisible to every other
    # check. Detect them explicitly and fail — this is the gate that keeps the
    # board unique.
    dup_epics = {t: ids for t, ids in epic_title_ids.items() if len(ids) > 1}
    dup_issues = {c: ids for c, ids in issue_code_ids.items() if len(ids) > 1}
    for t, wids in sorted(dup_epics.items()):
        fails.append(f"Duplicate Epic on board '{t}': work items {sorted(wids)} (run `dedup --go`)")
    for c, wids in sorted(dup_issues.items()):
        fails.append(f"Duplicate Issue on board {c}: work items {sorted(wids)} (run `dedup --go`)")

    if len(b_epics) != len(want_epics):
        fails.append(f"Epic count: board {len(b_epics)} != backlog {len(want_epics)}")

    missing = sorted(set(want_issues) - set(b_issues))
    extra = sorted(set(b_issues) - set(want_issues))
    if missing:
        fails.append(f"Issues in backlog but missing on board: {missing}")
    if extra:
        fails.append(f"Issues on board but not in backlog: {extra}")

    for code in sorted(set(want_issues) & set(b_issues)):
        c, b = want_issues[code], b_issues[code]
        if c["title"] != b["title"]:
            fails.append(f"{code} title mismatch: board {b['title']!r} != backlog {c['title']!r}")
        if htmlfmt.norm(c["desc"]) != htmlfmt.norm(b["desc"]):
            fails.append(f"{code} description out of sync")

    # State never comes from the backlog — it has no state column — so this
    # checks the hierarchy against itself. Azure DevOps does not cascade state,
    # so a closed Epic can sit above open Issues and Tasks indefinitely.
    downward, upward = _drift(rec, kids, cfg.states["done"])
    task_type = cfg.types["task"]
    board_tasks = {
        pid: {rec[c]["title"] for c in children if rec[c]["type"] == task_type}
        for pid, children in kids.items()
    }

    checked = 0
    for code in sorted(set(bullets) & set(b_issues)):
        desired = {htmlfmt.plain(x)[:tmax] for x in bullets[code]}
        actual = board_tasks.get(b_issues[code]["id"], set())
        miss = desired - actual
        ext = actual - desired
        if miss:
            fails.append(f"{code} missing {len(miss)} Task(s): " + "; ".join(sorted(miss)[:3])[:160])
        if ext:
            fails.append(f"{code} extra {len(ext)} Task(s): " + "; ".join(sorted(ext)[:3])[:160])
        checked += 1

    # State never comes from the backlog — it has no state column — so this
    # checks the hierarchy against itself. Azure DevOps does not cascade state,
    # so a closed Epic can sit above open Issues and Tasks indefinitely.
    by_parent = _group_by_ancestor(downward)
    for pid in sorted(by_parent):
        fails.append(
            f"{rec[pid]['type']} #{pid} is {cfg.states['done']} with "
            f"{len(by_parent[pid])} open descendant(s) (run `close-children --go`)"
        )

    print("=" * 60)
    print(f"ADO TRUTH AUDIT — {cfg.org}/{cfg.project}")
    print("=" * 60)
    print(f"Epics      : board {len(b_epics):>3}  / backlog {len(want_epics):>3}")
    print(f"Issues     : board {len(b_issues):>3}  / backlog {len(want_issues):>3}")
    print(f"Task-parity: checked {checked} issues against backlog bullets")
    print(f"State      : {len(by_parent)} done parent(s) with {len(downward)} open descendant(s)")
    if upward:
        names = ", ".join(f"#{w}" for w in sorted(upward)[:5])
        print(f"             {len(upward)} parent(s) with every child done: {names} (review)")
    n_dups = sum(len(ids) - 1 for ids in epic_title_ids.values() if len(ids) > 1)
    n_dups += sum(len(ids) - 1 for ids in issue_code_ids.values() if len(ids) > 1)
    print(f"Duplicates : {n_dups} extra work item(s) sharing a code/title")
    print("-" * 60)
    if fails:
        print(f"RESULT: FAIL ({len(fails)} problem(s))")
        for f in fails:
            print(f"  x {f}")
        return 1
    print("RESULT: PASS — board is in sync with the backlog")
    return 0
