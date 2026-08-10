from email.message import Message
import io
import unittest
from unittest.mock import patch, MagicMock
import urllib.error
import urllib.request

from ado_board_sync.config import Config
from ado_board_sync.client import Client


class MockResponse:
    def __init__(self, status, body):
        self.status = status
        self.body = body.encode() if isinstance(body, str) else body

    def read(self):
        return self.body

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        pass


def make_headers(headers_dict):
    msg = Message()
    for k, v in headers_dict.items():
        msg[k] = v
    return msg


class ClientResilienceTest(unittest.TestCase):
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

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_idempotent_get_retries_on_transport_error_then_succeeds(self, mock_sleep, mock_urlopen):
        # First attempt raises URLError, second succeeds
        mock_urlopen.side_effect = [
            urllib.error.URLError("connection reset"),
            MockResponse(200, '{"id": 123}')
        ]

        status, body = self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")

        self.assertEqual(status, 200)
        self.assertEqual(body, {"id": 123})
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_idempotent_get_propagates_transport_error_after_exhaustion(self, mock_sleep, mock_urlopen):
        # All attempts fail with URLError
        mock_urlopen.side_effect = urllib.error.URLError("persistent connection failure")

        with self.assertRaises(urllib.error.URLError):
            self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")

        self.assertEqual(mock_urlopen.call_count, 4)  # 1 original + 3 retries
        self.assertEqual(mock_sleep.call_count, 3)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_non_idempotent_create_does_not_retry_on_transport_error(self, mock_sleep, mock_urlopen):
        # Write request raises URLError
        mock_urlopen.side_effect = urllib.error.URLError("connection reset during POST")

        with self.assertRaises(urllib.error.URLError):
            self.client._req("POST", "https://dev.azure.com/demo-org/_apis/wit/workitems/$Issue", body=[])

        self.assertEqual(mock_urlopen.call_count, 1)  # No retries on writes
        mock_sleep.assert_not_called()

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_wiql_post_is_treated_as_idempotent_and_retried(self, mock_sleep, mock_urlopen):
        mock_urlopen.side_effect = [
            urllib.error.URLError("connection timed out"),
            MockResponse(200, '{"workItems": []}')
        ]

        status, body = self.client._req("POST", "https://dev.azure.com/demo-org/_apis/wit/wiql", body={})

        self.assertEqual(status, 200)
        self.assertEqual(body, {"workItems": []})
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_http_429_retried_for_writes_and_honors_retry_after(self, mock_sleep, mock_urlopen):
        # HTTP 429 returns with Retry-After header
        fp1 = io.BytesIO(b'{"error": "rate limit"}')
        err = urllib.error.HTTPError(
            "https://dev.azure.com/demo-org/_apis/wit/workitems/$Issue",
            429, "Too Many Requests", make_headers({"Retry-After": "0.5"}), fp1
        )
        mock_urlopen.side_effect = [
            err,
            MockResponse(201, '{"id": 999}')
        ]

        status, body = self.client._req("POST", "https://dev.azure.com/demo-org/_apis/wit/workitems/$Issue", body=[])

        self.assertEqual(status, 201)
        self.assertEqual(body, {"id": 999})
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.5)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_http_502_503_504_retried_only_for_reads(self, mock_sleep, mock_urlopen):
        # 503 is retried for GET
        fp1 = io.BytesIO(b"Service Unavailable")
        err = urllib.error.HTTPError(
            "https://dev.azure.com/demo-org/_apis/wit/workitems/123",
            503, "Service Unavailable", make_headers({}), fp1
        )
        mock_urlopen.side_effect = [
            err,
            MockResponse(200, '{"id": 123}')
        ]

        status, body = self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")
        self.assertEqual(status, 200)
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

        # 503 is NOT retried for POST creates
        mock_urlopen.reset_mock()
        mock_sleep.reset_mock()
        fp2 = io.BytesIO(b"Service Unavailable")
        err2 = urllib.error.HTTPError(
            "https://dev.azure.com/demo-org/_apis/wit/workitems/$Issue",
            503, "Service Unavailable", make_headers({}), fp2
        )
        mock_urlopen.side_effect = err2

        status, body = self.client._req("POST", "https://dev.azure.com/demo-org/_apis/wit/workitems/$Issue", body=[])
        self.assertEqual(status, 503)
        self.assertEqual(body, "Service Unavailable")
        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_other_http_errors_not_retried(self, mock_sleep, mock_urlopen):
        # 400 Bad Request is not retried
        fp = io.BytesIO(b"Bad Request")
        err = urllib.error.HTTPError(
            "https://dev.azure.com/demo-org/_apis/wit/workitems/123",
            400, "Bad Request", make_headers({}), fp
        )
        mock_urlopen.side_effect = err

        status, body = self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")
        self.assertEqual(status, 400)
        self.assertEqual(body, "Bad Request")
        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_negative_max_retries_defaults_to_zero_attempts(self, mock_sleep, mock_urlopen):
        self.cfg.max_retries = -5
        mock_urlopen.side_effect = urllib.error.URLError("connection reset")

        with self.assertRaises(urllib.error.URLError):
            self.client._req("GET", "https://dev.azure.com/demo-org/_apis/wit/workitems/123")

        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_patch_retries_on_transport_error_by_default(self, mock_sleep, mock_urlopen):
        # client.patch() sets a fixed field value on a known work item -> idempotent, so the default
        # safe_to_retry=True should survive a transient connection drop just like a GET does.
        mock_urlopen.side_effect = [
            urllib.error.URLError("connection reset"),
            MockResponse(200, '{"id": 907}'),
        ]

        status, body = self.client.patch(
            907, [{"op": "add", "path": "/fields/System.IterationPath", "value": "DemoProject\\Sprint 4"}]
        )

        self.assertEqual(status, 200)
        self.assertEqual(body, {"id": 907})
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_patch_retries_on_read_timeout(self, mock_sleep, mock_urlopen):
        # TimeoutError is a subclass of OSError, the exact failure mode seen mid-cascade against a flaky
        # connection: the request reached the server but the response read timed out.
        mock_urlopen.side_effect = [
            TimeoutError("The read operation timed out"),
            MockResponse(200, '{"id": 907}'),
        ]

        status, body = self.client.patch(
            907, [{"op": "add", "path": "/fields/System.AssignedTo", "value": "okyeboah@calbank.net"}]
        )

        self.assertEqual(status, 200)
        self.assertEqual(mock_urlopen.call_count, 2)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_patch_opts_out_of_retry_when_told_unsafe(self, mock_sleep, mock_urlopen):
        # A hypothetical non-fixed-value patch (e.g. a relations array-append) must not be blindly retried.
        mock_urlopen.side_effect = urllib.error.URLError("connection reset")

        with self.assertRaises(urllib.error.URLError):
            self.client.patch(907, [{"op": "add", "path": "/relations/-", "value": {}}], safe_to_retry=False)

        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_create_is_unaffected_and_still_not_retried(self, mock_sleep, mock_urlopen):
        # create() must keep its conservative default: a retried POST could create a duplicate work item.
        mock_urlopen.side_effect = urllib.error.URLError("connection reset during POST")

        with self.assertRaises(urllib.error.URLError):
            self.client.create("Issue", [])

        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_patch_retries_on_502_503_504(self, mock_sleep, mock_urlopen):
        fp = io.BytesIO(b"Service Unavailable")
        err = urllib.error.HTTPError(
            "https://dev.azure.com/demo-org/_apis/wit/workitems/907",
            503, "Service Unavailable", make_headers({}), fp
        )
        mock_urlopen.side_effect = [err, MockResponse(200, '{"id": 907}')]

        status, body = self.client.patch(907, [{"op": "add", "path": "/fields/System.State", "value": "Done"}])

        self.assertEqual(status, 200)
        self.assertEqual(mock_urlopen.call_count, 2)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_delete_retries_on_transport_error_by_default(self, mock_sleep, mock_urlopen):
        # A repeat DELETE by known id after a dropped connection either re-succeeds or 404s -
        # neither duplicates or corrupts anything, so it should retry like a GET does.
        mock_urlopen.side_effect = [
            urllib.error.URLError("connection reset"),
            MockResponse(204, ""),
        ]

        status, body = self.client.delete(907)

        self.assertEqual(status, 204)
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.01)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_delete_opts_out_of_retry_when_told_unsafe(self, mock_sleep, mock_urlopen):
        mock_urlopen.side_effect = urllib.error.URLError("connection reset")

        with self.assertRaises(urllib.error.URLError):
            self.client.delete(907, safe_to_retry=False)

        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_ensure_iteration_date_update_retries_on_transport_error(self, mock_sleep, mock_urlopen):
        # GET (exists check) misses, POST (create) hits 409 (already exists), then the PATCH that
        # syncs its dates must survive a transient connection drop -- this call used to bypass the
        # Client.patch() fix entirely by calling self._req("PATCH", ...) directly.
        fp = io.BytesIO(b'{"message": "already exists"}')
        conflict = urllib.error.HTTPError(
            "https://dev.azure.com/demo-org/_apis/wit/classificationnodes/iterations",
            409, "Conflict", make_headers({}), fp
        )
        mock_urlopen.side_effect = [
            MockResponse(404, ""),  # GET: node not cached/found yet
            conflict,               # POST create: race, node already exists
            urllib.error.URLError("connection reset"),  # PATCH attempt 1: transport drop
            MockResponse(200, '{"identifier": "iter-guid-1"}'),  # PATCH attempt 2: succeeds
        ]

        ok, ident, note = self.client.ensure_iteration("Sprint 4", "2026-08-10", "2026-08-21")

        self.assertTrue(ok)
        self.assertEqual(ident, "iter-guid-1")
        self.assertEqual(note, "exists; dates synced")
        self.assertEqual(mock_urlopen.call_count, 4)

    @patch("urllib.request.urlopen")
    @patch("time.sleep")
    def test_add_team_iteration_retries_on_transport_error(self, mock_sleep, mock_urlopen):
        # Idempotent by ADO's own contract (400 = "already in team"), so a transport failure must
        # not be left unretried either.
        mock_urlopen.side_effect = [
            urllib.error.URLError("connection reset"),
            MockResponse(200, '{}'),
        ]

        ok, note = self.client.add_team_iteration("DemoProject Team", "iter-guid-1")

        self.assertTrue(ok)
        self.assertEqual(note, "added to team")
        self.assertEqual(mock_urlopen.call_count, 2)


if __name__ == "__main__":
    unittest.main()
