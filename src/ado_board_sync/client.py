"""Thin Azure DevOps REST client (stdlib only).

The PAT is held only inside the Basic auth header and is never logged.
"""
import base64
import http.client
import json
import threading
import time
import urllib.parse


class Client:
    def __init__(self, cfg, pat):
        self.cfg = cfg
        self._auth = "Basic " + base64.b64encode(f":{pat}".encode()).decode()
        # Every request in one run targets the same host (dev.azure.com), so one
        # keep-alive connection is reused across the whole command instead of paying a
        # fresh TCP+TLS handshake per call. That per-call handshake -- not packet loss --
        # is what makes a multi-hundred-item sync feel slow even on a healthy network:
        # commands like resync-tasks/sprints/assign issue dozens to hundreds of sequential
        # requests, and each one used to open and tear down its own connection.
        #
        # The connection is stored per thread (http.client connections are not
        # thread-safe): concurrent apply phases hand each worker thread its own socket,
        # and every one of them still reuses it across all of that thread's requests.
        self._local = threading.local()

    def close(self):
        """Release this thread's keep-alive connection, if one is open."""
        conn = getattr(self._local, "conn", None)
        if conn is not None:
            conn.close()
        self._local.conn = None
        self._local.key = None

    def _connection(self, scheme, netloc, timeout):
        key = (scheme, netloc, timeout)
        conn = getattr(self._local, "conn", None)
        if conn is not None and getattr(self._local, "key", None) != key:
            self.close()
            conn = None
        if conn is None:
            cls = http.client.HTTPSConnection if scheme == "https" else http.client.HTTPConnection
            conn = cls(netloc, timeout=timeout)
            self._local.conn = conn
            self._local.key = key
        return conn

    @staticmethod
    def _backoff(attempt, backoff):
        return backoff * (2 ** attempt)

    @staticmethod
    def _retry_delay(status, resp, attempt, backoff):
        """Seconds to wait before retrying a retriable non-2xx response."""
        if status == 429:
            retry_after = resp.getheader("Retry-After")
            if retry_after:
                try:
                    return float(retry_after)
                except ValueError:
                    pass
        return Client._backoff(attempt, backoff)

    def _req(self, method, url, body=None, ctype="application/json", timeout=None, safe_to_retry=None):
        """Execute an HTTP request with built-in network resilience.

        GET and WIQL POST are idempotent and always retried on transient transport errors and on
        429/502/503/504. Every other method is retried only on 429 (rejected before any side effect)
        unless the caller passes ``safe_to_retry=True`` — reserved for writes that produce the same
        end state whether applied once or N times (e.g. a fixed-value field PATCH), never for writes
        whose retried effect could differ from the first attempt (creates, array-append patches).
        """
        data = json.dumps(body).encode() if body is not None else None

        # Read parameters from config, falling back to defaults if not set
        max_retries = max(0, getattr(self.cfg, "max_retries", 0))
        backoff = getattr(self.cfg, "backoff", 1.5)
        if timeout is None:
            timeout = getattr(self.cfg, "timeout", 20)

        # Auto-detect idempotency by method/url; an explicit safe_to_retry overrides the guess.
        is_idempotent = method == "GET" or (method == "POST" and "/wit/wiql" in url)
        retriable = is_idempotent if safe_to_retry is None else safe_to_retry
        total_attempts = 1 + max_retries

        parts = urllib.parse.urlsplit(url)
        target = parts.path + (f"?{parts.query}" if parts.query else "")
        headers = {"Authorization": self._auth, "Accept": "application/json"}
        if data is not None:
            headers["Content-Type"] = ctype

        for attempt in range(total_attempts):
            conn = self._connection(parts.scheme, parts.netloc, timeout)
            try:
                conn.request(method, target, body=data, headers=headers)
                resp = conn.getresponse()
                raw = resp.read()
                status = resp.status
            except (http.client.HTTPException, OSError):
                # Transport-level failure (dropped/reset/idle-closed connection, timeout, refused
                # connection, ...). The connection is no longer trustworthy either way, so drop it --
                # the next attempt (if any) opens a fresh one.
                self.close()
                if retriable and attempt < total_attempts - 1:
                    time.sleep(self._backoff(attempt, backoff))
                    continue
                raise
            else:
                if status < 400:
                    return status, (json.loads(raw) if raw else {})
                # Non-2xx response.
                # 429 is retriable for all requests; 502/503/504 are retriable only for idempotent/safe ones.
                is_retriable = status == 429 or (retriable and status in (502, 503, 504))
                if is_retriable and attempt < total_attempts - 1:
                    time.sleep(self._retry_delay(status, resp, attempt, backoff))
                    continue
                else:
                    return status, raw.decode()

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

    def patch(self, wid, ops, safe_to_retry=None):
        """PATCH fields on an already-identified work item.

        Retriable by default unless any op appends to an array (path ending in ``/-``, e.g.
        ``/relations/-``) — that shape would duplicate on retry, so it's excluded automatically.
        Pass ``safe_to_retry`` explicitly to override the auto-detection.
        """
        cfg = self.cfg
        url = f"{cfg.org_url}/wit/workitems/{wid}?api-version={cfg.api_version}"
        if safe_to_retry is None:
            safe_to_retry = all(not op.get("path", "").endswith("/-") for op in ops)
        return self._req(
            "PATCH", url, ops, ctype="application/json-patch+json", safe_to_retry=safe_to_retry
        )

    def delete(self, wid, safe_to_retry=True):
        """DELETE an already-identified work item by id.

        Defaults to retriable: a repeat DELETE after a dropped connection either succeeds again
        (item wasn't actually removed yet) or 404s (it was) — neither creates a duplicate or a
        corrupted state, unlike a retried create.
        """
        cfg = self.cfg
        return self._req(
            "DELETE", f"{cfg.org_url}/wit/workitems/{wid}?api-version={cfg.api_version}",
            safe_to_retry=safe_to_retry,
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
            # A fixed-value {name, attributes} set on a known node, same idempotent shape as
            # Client.patch()'s work-item field writes -> safe to retry on a transport failure.
            st2, r2 = self._req(
                "PATCH", f"{base}/{enc}?api-version={cfg.api_version}", body, safe_to_retry=True
            )
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
        # Idempotent by ADO's own contract (see the 400 handling below), so a repeat call after a
        # transport failure is harmless -> safe to retry.
        st, r = self._req("POST", url, {"id": identifier}, safe_to_retry=True)
        if st in (200, 201):
            return True, "added to team"
        if st == 400:  # ADO returns 400 when the iteration is already selected
            return True, "already in team"
        return False, f"{st} {r}"
