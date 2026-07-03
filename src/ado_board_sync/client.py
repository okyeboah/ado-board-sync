"""Thin Azure DevOps REST client (stdlib only).

The PAT is held only inside the Basic auth header and is never logged.
"""
import base64
import json
import time
import urllib.error
import urllib.parse
import urllib.request


class Client:
    def __init__(self, cfg, pat):
        self.cfg = cfg
        self._auth = "Basic " + base64.b64encode(f":{pat}".encode()).decode()

    def _req(self, method, url, body=None, ctype="application/json", timeout=None):
        """Execute an HTTP request with built-in network resilience.

        Idempotency Policy:
        - Idempotent/Read requests (GET, or POST queries to `/wit/wiql`) are freely retried on both
          transient transport/network failures (URLError, OSError, TimeoutError) and transient HTTP status
          codes (429, 502, 503, 504).
        - Non-idempotent/Write requests (creates, updates, deletes) are never auto-retried on transient
          transport/network errors or 5xx status codes. This is because a connection drop or reset can occur
          after the server successfully processes the mutation but before sending the response, leading to
          duplicate resources or corrupted states. Non-idempotent writes are, however, safely retried on
          HTTP 429 (Too Many Requests), where the request is guaranteed to have been rejected by the rate
          limiter without side effects.
        """
        data = json.dumps(body).encode() if body is not None else None

        # Read parameters from config, falling back to defaults if not set
        max_retries = max(0, getattr(self.cfg, "max_retries", 0))
        backoff = getattr(self.cfg, "backoff", 1.5)
        if timeout is None:
            timeout = getattr(self.cfg, "timeout", 20)

        # Check if the request is idempotent
        is_idempotent = method == "GET" or (method == "POST" and "/wit/wiql" in url)
        total_attempts = 1 + max_retries

        for attempt in range(total_attempts):
            r = urllib.request.Request(url, data=data, method=method)
            r.add_header("Authorization", self._auth)
            r.add_header("Accept", "application/json")
            if data is not None:
                r.add_header("Content-Type", ctype)
            try:
                with urllib.request.urlopen(r, timeout=timeout) as resp:
                    raw = resp.read().decode()
                    return resp.status, (json.loads(raw) if raw else {})
            except urllib.error.HTTPError as e:
                # HTTPError represents non-2xx HTTP responses.
                # 429 is retriable for all requests; 502/503/504 are retriable only for idempotent ones.
                is_retriable = e.code == 429 or (is_idempotent and e.code in (502, 503, 504))
                if is_retriable and attempt < total_attempts - 1:
                    delay = None
                    if e.code == 429:
                        retry_after = e.headers.get("Retry-After")
                        if retry_after:
                            try:
                                delay = float(retry_after)
                            except ValueError:
                                pass
                    if delay is None:
                        delay = backoff * (2 ** attempt)
                    time.sleep(delay)
                    continue
                else:
                    return e.code, e.read().decode()
            except (urllib.error.URLError, OSError) as e:
                # Transient transport errors are retried only for idempotent requests
                if is_idempotent and attempt < total_attempts - 1:
                    delay = backoff * (2 ** attempt)
                    time.sleep(delay)
                    continue
                else:
                    raise

        raise RuntimeError("Request failed: retry loop completed without returning")

    # --- queries -----------------------------------------------------------
    def wiql(self, where):
        """Run ``SELECT [System.Id] FROM WorkItems WHERE <where>`` -> [ids]."""
        cfg = self.cfg
        q = {"query": f"SELECT [System.Id] FROM WorkItems WHERE {where}"}
        st, r = self._req("POST", f"{cfg.base_url}/wit/wiql?api-version={cfg.api_version}", q)
        if st != 200 or not isinstance(r, dict) or "workItems" not in r:
            raise RuntimeError(f"WIQL failed: {st} {r}")
        return [w["id"] for w in r["workItems"]]

    def get_items(self, ids, fields):
        """Batch-fetch work items (200 at a time) with the given field list."""
        cfg = self.cfg
        out = []
        for i in range(0, len(ids), 200):
            chunk = ids[i:i + 200]
            if not chunk:
                continue
            url = (
                f"{cfg.org_url}/wit/workitems?ids={','.join(map(str, chunk))}"
                f"&fields={','.join(fields)}&api-version={cfg.api_version}"
            )
            st, r = self._req("GET", url)
            if st != 200 or not isinstance(r, dict):
                raise RuntimeError(f"Batch get failed: {st} {r}")
            out.extend(r.get("value", []))
        return out

    def get_item(self, wid, expand=None):
        cfg = self.cfg
        url = f"{cfg.org_url}/wit/workitems/{wid}?api-version={cfg.api_version}"
        if expand:
            url = (
                f"{cfg.org_url}/wit/workitems/{wid}"
                f"?$expand={expand}&api-version={cfg.api_version}"
            )
        return self._req("GET", url)

    # --- mutations ---------------------------------------------------------
    def create(self, wtype, ops):
        cfg = self.cfg
        encoded = urllib.parse.quote(f"${wtype}")
        url = f"{cfg.base_url}/wit/workitems/{encoded}?api-version={cfg.api_version}"
        return self._req("POST", url, ops, ctype="application/json-patch+json")

    def patch(self, wid, ops):
        cfg = self.cfg
        url = f"{cfg.org_url}/wit/workitems/{wid}?api-version={cfg.api_version}"
        return self._req("PATCH", url, ops, ctype="application/json-patch+json")

    def delete(self, wid):
        cfg = self.cfg
        return self._req(
            "DELETE", f"{cfg.org_url}/wit/workitems/{wid}?api-version={cfg.api_version}"
        )

    def parent_link(self, parent_id):
        """A relations patch value linking a new item to its parent."""
        return {
            "rel": "System.LinkTypes.Hierarchy-Reverse",
            "url": f"{self.cfg.org_url}/wit/workItems/{parent_id}",
        }

    # --- iterations (sprints) ---------------------------------------------
    def ensure_iteration(self, name, start=None, finish=None):
        """Create a sprint iteration node under the project root, or update the
        dates of an existing one. Returns ``(ok, identifier, note)`` where
        ``identifier`` is the node GUID needed to add it to a team's sprint view.
        """
        cfg = self.cfg
        attrs = {}
        if start:
            attrs["startDate"] = f"{start}T00:00:00Z"
        if finish:
            attrs["finishDate"] = f"{finish}T00:00:00Z"
        body = {"name": name}
        if attrs:
            body["attributes"] = attrs

        base = f"{cfg.base_url}/wit/classificationnodes/iterations"
        enc = urllib.parse.quote(name)
        st_get, r_get = self._req("GET", f"{base}/{enc}?api-version={cfg.api_version}")
        if st_get == 200 and isinstance(r_get, dict):
            return True, r_get.get("identifier"), "exists"

        st, r = self._req("POST", f"{base}?api-version={cfg.api_version}", body)
        if st in (200, 201):
            return True, (r.get("identifier") if isinstance(r, dict) else None), "created"
        if st == 409:  # node already exists -> PATCH to make its dates authoritative
            st2, r2 = self._req("PATCH", f"{base}/{enc}?api-version={cfg.api_version}", body)
            if st2 == 200:
                return True, (r2.get("identifier") if isinstance(r2, dict) else None), "exists; dates synced"
            return False, None, f"update failed: {st2} {r2}"
        return False, None, f"create failed: {st} {r}"

    def default_team(self):
        """Best-guess team to own the sprints: the ``<Project> Team`` default if
        present, else the first team. Returns the team name or ``None``."""
        cfg = self.cfg
        url = f"{cfg.org_url}/projects/{cfg.project}/teams?api-version={cfg.api_version}"
        st, r = self._req("GET", url)
        if st == 200 and isinstance(r, dict) and r.get("value"):
            want = f"{cfg.project} team".lower()
            for t in r["value"]:
                if t.get("name", "").lower() == want:
                    return t["name"]
            return r["value"][0].get("name")
        return None

    def add_team_iteration(self, team, identifier):
        """Add an existing iteration (by GUID) to a team's selected sprints so it
        shows up in the team's Sprints view. Returns ``(ok, note)``."""
        cfg = self.cfg
        team_enc = urllib.parse.quote(team)
        url = (
            f"https://dev.azure.com/{cfg.org}/{cfg.project}/{team_enc}"
            f"/_apis/work/teamsettings/iterations?api-version={cfg.api_version}"
        )
        st, r = self._req("POST", url, {"id": identifier})
        if st in (200, 201):
            return True, "added to team"
        if st == 400:  # ADO returns 400 when the iteration is already selected
            return True, "already in team"
        return False, f"{st} {r}"
