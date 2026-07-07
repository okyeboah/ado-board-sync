"""In-memory Azure DevOps client used by command tests (no network)."""

FORWARD = "System.LinkTypes.Hierarchy-Forward"
REVERSE = "System.LinkTypes.Hierarchy-Reverse"


class FakeClient:
    def __init__(self, cfg):
        self.cfg = cfg
        self.items = {}          # id -> {"fields": {...}, "relations": [...]}
        self._next = 1000
        self._type_names = set(cfg.types.values())
        self.iterations = {}     # name -> identifier
        self.team_iterations = set()  # identifiers added to the team's sprint view

    # --- seeding helpers ---------------------------------------------------
    def add_item(self, wtype, title, desc="", parent=None, state=None, assigned_to=None):
        wid = self._new_id()
        fields = {
            "System.Title": title,
            "System.Description": desc,
            "System.WorkItemType": wtype,
        }
        if state is not None:
            fields["System.State"] = state
        if assigned_to is not None:
            fields["System.AssignedTo"] = assigned_to
        self.items[wid] = {"fields": fields, "relations": []}
        if parent is not None:
            self._link(parent, wid)
        return wid

    def _new_id(self):
        self._next += 1
        return self._next

    def _link(self, parent, child):
        self.items[child]["relations"].append(
            {"rel": REVERSE, "url": f"{self.cfg.org_url}/wit/workItems/{parent}"}
        )
        self.items[parent]["relations"].append(
            {"rel": FORWARD, "url": f"{self.cfg.org_url}/wit/workItems/{child}"}
        )

    # --- Client interface --------------------------------------------------
    def wiql(self, where):
        types = [t for t in self._type_names if f"'{t}'" in where]
        out = []
        for wid, it in self.items.items():
            if not types or it["fields"]["System.WorkItemType"] in types:
                out.append(wid)
        return sorted(out)

    def get_items(self, ids, fields):
        out = []
        for wid in ids:
            if wid not in self.items:
                continue
            src = self.items[wid]["fields"]
            out.append({"id": wid, "fields": {f: src.get(f, "") for f in fields}})
        return out

    def get_item(self, wid, expand=None):
        it = self.items[wid]
        return 200, {"id": wid, "fields": dict(it["fields"]), "relations": list(it["relations"])}

    def create(self, wtype, ops):
        wid = self._new_id()
        self.items[wid] = {
            "fields": {"System.WorkItemType": wtype},
            "relations": [],
        }
        for op in ops:
            path = op["path"]
            if path.startswith("/fields/"):
                self.items[wid]["fields"][path[len("/fields/"):]] = op["value"]
            elif path == "/relations/-":
                rel = op["value"]
                self.items[wid]["relations"].append(rel)
                if rel["rel"] == REVERSE:
                    parent = int(rel["url"].split("/")[-1])
                    self.items[parent]["relations"].append(
                        {"rel": FORWARD, "url": f"{self.cfg.org_url}/wit/workItems/{wid}"}
                    )
        return 201, {"id": wid}

    def patch(self, wid, ops) -> tuple:
        for op in ops:
            if op["path"].startswith("/fields/"):
                self.items[wid]["fields"][op["path"][len("/fields/"):]] = op["value"]
        return 200, {}

    def delete(self, wid):
        self.items.pop(wid, None)
        for it in self.items.values():
            it["relations"] = [
                r for r in it["relations"] if int(r["url"].split("/")[-1]) != wid
            ]
        return 204, {}

    def parent_link(self, parent_id):
        return {"rel": REVERSE, "url": f"{self.cfg.org_url}/wit/workItems/{parent_id}"}

    # --- iterations --------------------------------------------------------
    def ensure_iteration(self, name, start=None, finish=None):
        if name in self.iterations:
            return True, self.iterations[name], "exists; dates synced"
        ident = f"iter-{name}"
        self.iterations[name] = ident
        return True, ident, "created"

    def default_team(self):
        return f"{self.cfg.project} Team"

    def add_team_iteration(self, team, identifier):
        self.team_iterations.add(identifier)
        return True, "added to team"
