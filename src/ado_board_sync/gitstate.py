"""Advance backlog Issues from their start state to their working state using git evidence.

The board understates progress: Issues sit in the configured start state while
their feature branch already holds real commits ahead of the base ref. This
command reads local repositories, matches branch names against Issue codes, and
moves those still-in-start-state Issues onward.

Two safety rails come with it:

- Only the start -> working transition is automatic. Once work has demonstrably
  begun, that state is true whatever happens next, so the write is always safe.
- ``done`` is never set from git evidence. Merge detection fails in both
  directions: an empty branch bearing a ticket code in the wrong repository
  reads as merged, and a squash-merged branch reads as unmerged. Completing a
  work item is a human decision backed by a merged pull request, so it stays one.

Dry-run by default; pass ``--go`` to apply.
"""
import subprocess

from . import parser
from .commands import _apply, _issue_map


def _sh(repo, *args):
    result = subprocess.run(("git", "-C", repo) + args, capture_output=True, text=True)
    return result.stdout.strip() if result.returncode == 0 else None


def _repo_ready(repo, fetch):
    """True if the repository exists and its refs are trustworthy.

    A failed fetch is treated as unusable rather than skipped silently: probing
    stale refs would read as unstarted work that has actually begun.
    """
    if _sh(repo, "rev-parse", "--git-dir") is None:
        print(f"  ! {repo}: not a git repository, skipped")
        return False
    if fetch and subprocess.run(
        ("git", "-C", repo, "fetch", "--prune", "--quiet"), capture_output=True
    ).returncode != 0:
        print(f"  ! {repo}: fetch failed, skipped (refs would be stale)")
        return False
    return True


def _branches(repo):
    listing = _sh(repo, "branch", "-a", "--format=%(refname:short)")
    return (listing or "").splitlines()


def _ahead_count(repo, base, branch):
    """Commits on ``branch`` not reachable from ``base``, or None when unknown."""
    count = _sh(repo, "rev-list", "--count", f"{base}..{branch}")
    try:
        return int(count)
    except (TypeError, ValueError):
        return None


def started_by_code(code_re, repos, base, fetch=True):
    """Map each Issue code with evidence to its proving branches.

    Evidence for ``code`` is any branch whose name contains it and which holds
    commits beyond ``base``. Every successful probe costs no network round trip;
    the git activity is local.
    """
    evidence = {}
    for repo in repos:
        if not _repo_ready(repo, fetch):
            continue
        for branch in _branches(repo):
            m = code_re.search(branch)
            if not m:
                continue
            n = _ahead_count(repo, base, branch)
            if n:
                evidence.setdefault(m.group(1).upper(), []).append((repo, branch, n))
    return evidence


def _states_ready(cfg):
    todo = cfg.states.get("todo")
    doing = cfg.states.get("doing")
    if todo and doing:
        return todo, doing
    print(
        "advance needs the start and working state names of your process template. "
        "Set 'states.todo' and 'states.doing' in board.config.json — for example "
        '{"todo": "New", "doing": "Active"} (Agile/CMMI) or '
        '{"todo": "To Do", "doing": "Doing"} (Scrum).'
    )
    return None


def advance(cfg, client, args):
    states = _states_ready(cfg)
    if not states:
        return 1
    todo, doing = states

    backlog_codes = [
        it["code"] for it in parser.parse_board(cfg) if it["level"] == "issue"
    ]
    evidence = started_by_code(
        cfg.issue_code_re, args.repo, args.base, fetch=not args.no_fetch
    )

    # One batched read of every board Issue; matching works off titles exactly
    # like import/resync do, so Tasks citing other tickets' codes cannot match.
    code_item = _issue_map(cfg, client, "System.State")  # CODE -> (id, current state)

    plan = []      # (code, issue_id, branches summary)
    already = []   # codes with evidence whose Issue left the start state already
    ghost = []     # codes with evidence that are not Issues on this board
    for code in sorted(set(backlog_codes) & set(evidence)):
        entry = code_item.get(code)
        if entry is None:
            ghost.append(code)
            continue
        iid, state = entry
        if state == todo:
            branches = "; ".join(f"{b} (+{n})" for _, b, n in evidence[code][:3])
            plan.append((code, iid, branches))
        else:
            already.append(code)

    print(f"Issues with commit evidence ahead of {args.base}: "
          f"{len(plan)} to advance, {len(already)} already past '{todo}', "
          f"{len(ghost)} without a board item")
    for code, iid, branches in plan:
        print(f"  {code} (#{iid}): '{todo}' -> '{doing}'  [{branches}]")
    if ghost:
        print(f"WARN evidenced codes absent from the board: {ghost}")

    if not plan:
        print("\nNothing to advance.")
        return 0
    if not args.go:
        print("\n(dry-run; pass --go to apply)")
        return 0

    op = [{"op": "add", "path": "/fields/System.State", "value": doing}]
    jobs = [lambda iid=iid, op=op: client.patch(iid, op) for _code, iid, _b in plan]
    ok = fail = 0
    for (code, iid, _branches), (st, r) in zip(plan, _apply(jobs)):
        if st == 200:
            ok += 1
        else:
            fail += 1
            print(f"  FAIL {code} #{iid} -> {doing}: {st} {r}")
    print(f"\nAdvanced {ok} Issue(s) to '{doing}'.")
    return 0 if fail == 0 else 1
