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


if __name__ == "__main__":
    unittest.main()
