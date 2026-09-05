import unittest
from unittest.mock import patch, MagicMock

from ado_board_sync.config import Config
from ado_board_sync.client import Client


class FakeHTTPResponse:
    """Stand-in for the object http.client.HTTPConnection.getresponse() returns."""

    def __init__(self, status, body, headers=None):
        self.status = status
        self._body = body.encode() if isinstance(body, str) else body
        self._headers = headers or {}

    def read(self):
        return self._body

    def getheader(self, name, default=None):
        return self._headers.get(name, default)


class ClientTestBase(unittest.TestCase):
    def setUp(self):
        self.cfg = Config({
            "org": "demo-org",
            "project": "DemoProject",
            "code_prefix": "PROJ",
            "max_retries": 3,
            "backoff": 0.01,  # Keep tests fast
            "timeout": 5,
        }, ".")
        self.client = Client(self.cfg, "secret-pat")

    @staticmethod
    def _mock_conn(mock_https_connection):
        """Every HTTPSConnection(...) construction returns this same mock, so tests can
        assert on it regardless of how many times the client (re)connects."""
        conn = MagicMock(name="conn")
        mock_https_connection.return_value = conn
        return conn


class ClientResilienceTest(ClientTestBase):
    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_idempotent_get_retries_on_transport_error_then_succeeds(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        # First attempt fails to even send; second attempt sends fine and gets a response.
        conn.request.side_effect = [OSError("connection reset"), None]
        conn.getresponse.side_effect = [FakeHTTPResponse(200, '{"id": 123}')]

        status, body = self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")

        self.assertEqual(status, 200)
        self.assertEqual(body, {"id": 123})
        self.assertEqual(conn.request.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_idempotent_get_propagates_transport_error_after_exhaustion(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = OSError("persistent connection failure")

        with self.assertRaises(OSError):
            self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")

        self.assertEqual(conn.request.call_count, 4)  # 1 original + 3 retries
        self.assertEqual(mock_sleep.call_count, 3)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_non_idempotent_create_does_not_retry_on_transport_error(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = OSError("connection reset during POST")

        with self.assertRaises(OSError):
            self.client._req("POST", "https://dev.azure.com/demo-org/_apis/wit/workitems/$Issue", body=[])

        self.assertEqual(conn.request.call_count, 1)  # No retries on writes
        mock_sleep.assert_not_called()

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_wiql_post_is_treated_as_idempotent_and_retried(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [OSError("connection timed out"), None]
        conn.getresponse.side_effect = [FakeHTTPResponse(200, '{"workItems": []}')]

        status, body = self.client._req("POST", "https://dev.azure.com/demo-org/_apis/wit/wiql", body={})

        self.assertEqual(status, 200)
        self.assertEqual(body, {"workItems": []})
        self.assertEqual(conn.request.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_http_429_retried_for_writes_and_honors_retry_after(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [None, None]
        conn.getresponse.side_effect = [
            FakeHTTPResponse(429, '{"error": "rate limit"}', {"Retry-After": "0.5"}),
            FakeHTTPResponse(201, '{"id": 999}'),
        ]

        status, body = self.client._req("POST", "https://dev.azure.com/demo-org/_apis/wit/workitems/$Issue", body=[])

        self.assertEqual(status, 201)
        self.assertEqual(body, {"id": 999})
        self.assertEqual(conn.request.call_count, 2)
        mock_sleep.assert_called_once_with(0.5)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_http_502_503_504_retried_only_for_reads(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)

        # 503 is retried for GET
        conn.request.side_effect = [None, None]
        conn.getresponse.side_effect = [
            FakeHTTPResponse(503, "Service Unavailable"),
            FakeHTTPResponse(200, '{"id": 123}'),
        ]

        status, body = self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")
        self.assertEqual(status, 200)
        self.assertEqual(conn.request.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

        # 503 is NOT retried for POST creates
        conn.request.reset_mock()
        mock_sleep.reset_mock()
        conn.request.side_effect = [None]
        conn.getresponse.side_effect = [FakeHTTPResponse(503, "Service Unavailable")]

        status, body = self.client._req("POST", "https://dev.azure.com/demo-org/_apis/wit/workitems/$Issue", body=[])
        self.assertEqual(status, 503)
        self.assertEqual(body, "Service Unavailable")
        self.assertEqual(conn.request.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_other_http_errors_not_retried(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [None]
        conn.getresponse.side_effect = [FakeHTTPResponse(400, "Bad Request")]

        status, body = self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")
        self.assertEqual(status, 400)
        self.assertEqual(body, "Bad Request")
        self.assertEqual(conn.request.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_negative_max_retries_defaults_to_zero_attempts(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        self.cfg.max_retries = -5
        conn.request.side_effect = OSError("connection reset")

        with self.assertRaises(OSError):
            self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")

        self.assertEqual(conn.request.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_patch_retries_on_transport_error_by_default(self, mock_sleep, mock_https_connection):
        # client.patch() sets a fixed field value on a known work item -> idempotent, so the default
        # safe_to_retry=True should survive a transient connection drop just like a GET does.
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [OSError("connection reset"), None]
        conn.getresponse.side_effect = [FakeHTTPResponse(200, '{"id": 907}')]

        status, body = self.client.patch(
            907, [{"op": "add", "path": "/fields/System.IterationPath", "value": "DemoProject\\Sprint 4"}]
        )

        self.assertEqual(status, 200)
        self.assertEqual(body, {"id": 907})
        self.assertEqual(conn.request.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_patch_retries_on_read_timeout(self, mock_sleep, mock_https_connection):
        # TimeoutError is an OSError subclass, the exact failure mode seen mid-cascade against a flaky
        # connection: the request reached the server but the response read timed out.
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [None, None]
        conn.getresponse.side_effect = [
            TimeoutError("The read operation timed out"),
            FakeHTTPResponse(200, '{"id": 907}'),
        ]

        status, body = self.client.patch(
            907, [{"op": "add", "path": "/fields/System.AssignedTo", "value": "okyeboah@calbank.net"}]
        )

        self.assertEqual(status, 200)
        self.assertEqual(conn.request.call_count, 2)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_patch_opts_out_of_retry_when_told_unsafe(self, mock_sleep, mock_https_connection):
        # A hypothetical non-fixed-value patch (e.g. a relations array-append) must not be blindly retried.
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = OSError("connection reset")

        with self.assertRaises(OSError):
            self.client.patch(907, [{"op": "add", "path": "/relations/-", "value": {}}], safe_to_retry=False)

        self.assertEqual(conn.request.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_create_is_unaffected_and_still_not_retried(self, mock_sleep, mock_https_connection):
        # create() must keep its conservative default: a retried POST could create a duplicate work item.
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = OSError("connection reset during POST")

        with self.assertRaises(OSError):
            self.client.create("Issue", [])

        self.assertEqual(conn.request.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_patch_retries_on_502_503_504(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [None, None]
        conn.getresponse.side_effect = [
            FakeHTTPResponse(503, "Service Unavailable"),
            FakeHTTPResponse(200, '{"id": 907}'),
        ]

        status, body = self.client.patch(907, [{"op": "add", "path": "/fields/System.State", "value": "Done"}])

        self.assertEqual(status, 200)
        self.assertEqual(conn.request.call_count, 2)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_delete_retries_on_transport_error_by_default(self, mock_sleep, mock_https_connection):
        # A repeat DELETE by known id after a dropped connection either re-succeeds or 404s -
        # neither duplicates or corrupts anything, so it should retry like a GET does.
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [OSError("connection reset"), None]
        conn.getresponse.side_effect = [FakeHTTPResponse(204, "")]

        status, body = self.client.delete(907)

        self.assertEqual(status, 204)
        self.assertEqual(conn.request.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_delete_opts_out_of_retry_when_told_unsafe(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = OSError("connection reset")

        with self.assertRaises(OSError):
            self.client.delete(907, safe_to_retry=False)

        self.assertEqual(conn.request.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_ensure_iteration_date_update_retries_on_transport_error(self, mock_sleep, mock_https_connection):
        # GET (exists check) misses, POST (create) hits 409 (already exists), then the PATCH that
        # syncs its dates must survive a transient connection drop -- this call used to bypass the
        # Client.patch() fix entirely by calling self._req("PATCH", ...) directly.
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [
            None,                                        # GET: sends fine
            None,                                        # POST create: sends fine
            OSError("connection reset"),                  # PATCH attempt 1: transport drop
            None,                                        # PATCH attempt 2: sends fine
        ]
        conn.getresponse.side_effect = [
            FakeHTTPResponse(404, ""),                                     # GET: node not found yet
            FakeHTTPResponse(409, '{"message": "already exists"}'),        # POST: race, already exists
            FakeHTTPResponse(200, '{"identifier": "iter-guid-1"}'),        # PATCH attempt 2: succeeds
        ]

        ok, ident, note = self.client.ensure_iteration("Sprint 4", "2026-08-10", "2026-08-21")

        self.assertTrue(ok)
        self.assertEqual(ident, "iter-guid-1")
        self.assertEqual(note, "exists; dates synced")
        self.assertEqual(conn.request.call_count, 4)
        # The connection survives across the GET and POST calls (no failure yet) and is only
        # torn down and reopened once, after the transport drop on the PATCH -- proof that a
        # transient failure resets exactly the broken connection, not the whole client.
        self.assertEqual(mock_https_connection.call_count, 2)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_add_team_iteration_retries_on_transport_error(self, mock_sleep, mock_https_connection):
        # Idempotent by ADO's own contract (400 = "already in team"), so a transport failure must
        # not be left unretried either.
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [OSError("connection reset"), None]
        conn.getresponse.side_effect = [FakeHTTPResponse(200, "{}")]

        ok, note = self.client.add_team_iteration("DemoProject Team", "iter-guid-1")

        self.assertTrue(ok)
        self.assertEqual(note, "added to team")
        self.assertEqual(conn.request.call_count, 2)


class ClientConnectionReuseTest(ClientTestBase):
    """Regression coverage for the keep-alive fix: users reported ado-board-sync feeling slow
    on a reliable network. The cause was that every single request opened and tore down its own
    TCP+TLS connection -- these tests pin down that one connection now survives across many
    requests, since a silent regression back to per-call connections would reintroduce the slowdown
    without failing anything functional."""

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_one_connection_serves_many_sequential_requests(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [None] * 5
        conn.getresponse.side_effect = [FakeHTTPResponse(200, '{"id": 1}') for _ in range(5)]

        for _ in range(5):
            status, _body = self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/1")
            self.assertEqual(status, 200)

        self.assertEqual(conn.request.call_count, 5)
        # Five requests, one physical connection: this is the whole point of the fix.
        self.assertEqual(mock_https_connection.call_count, 1)

    @patch("http.client.HTTPSConnection")
    @patch("time.sleep")
    def test_close_forces_the_next_request_to_reconnect(self, mock_sleep, mock_https_connection):
        conn = self._mock_conn(mock_https_connection)
        conn.request.side_effect = [None, None]
        conn.getresponse.side_effect = [
            FakeHTTPResponse(200, '{"id": 1}'),
            FakeHTTPResponse(200, '{"id": 1}'),
        ]

        self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/1")
        self.client.close()
        self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/1")

        self.assertEqual(conn.request.call_count, 2)
        self.assertEqual(mock_https_connection.call_count, 2)


if __name__ == "__main__":
    unittest.main()
