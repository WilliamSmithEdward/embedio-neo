"""Offline resource-observation controls; no server or real sockets required."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("resource_campaign", Path(__file__).with_name("h2_campaign.py"))
peer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(peer)


class StatsSocket:
    def __init__(self, chunks):
        self.chunks = iter(chunks)
        self.timeouts = []
        self.closed = False

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.closed = True

    def settimeout(self, value):
        self.timeouts.append(value)

    def sendall(self, data):
        pass

    def recv(self, size):
        return next(self.chunks)


def snapshot(active=0):
    return {"activeHandlers": active, "handles": 10, "managedBytes": 100}


class ResourceOracleTest(unittest.TestCase):
    def run_cases_main(self, before, after, expected):
        case = ("control", "offline", "MUST", lambda endpoint: ("conforms", "ok"))
        argv = ["h2_campaign.py", "cases", "--port", "1", "--stats-port", "2"]
        with patch.object(peer, "CASES", [case]), patch.object(peer, "stats", return_value=before), \
                patch.object(peer, "settle", return_value=after), patch.object(peer, "healthy", return_value=None), \
                patch.object(peer.sys, "argv", argv), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(SystemExit) as raised:
                peer.main()
        self.assertEqual(expected, raised.exception.code)

    def test_valid_observations_pass(self):
        self.run_cases_main(snapshot(), snapshot(), 0)

    def test_before_observation_failure_fails(self):
        self.run_cases_main({"error": "timeout", **snapshot()}, snapshot(), 1)

    def test_after_observation_failure_fails(self):
        self.run_cases_main(snapshot(), {"error": "timeout", **snapshot()}, 1)

    def test_unsettled_handlers_fail(self):
        self.run_cases_main(snapshot(), snapshot(1), 1)

    def test_unrequested_observations_remain_optional(self):
        self.assertIsNone(peer.resource_error(None, None))

    def test_normal_snapshot_and_connect_timeout_preserved(self):
        expected = snapshot()
        wire = b"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n" + json.dumps(expected).encode()
        sock = StatsSocket([wire, b""])
        with patch.object(peer.socket, "create_connection", return_value=sock) as connect:
            self.assertEqual(expected, peer.stats("example", 2))
        connect.assert_called_once_with(("example", 2), timeout=10)
        self.assertTrue(sock.closed)
        self.assertTrue(all(0 < value <= 10 for value in sock.timeouts))

    def test_chunked_snapshot_remains_supported(self):
        expected = snapshot()
        body = json.dumps(expected).encode()
        wire = (b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"
                + format(len(body), "x").encode() + b"\r\n" + body + b"\r\n0\r\n\r\n")
        sock = StatsSocket([wire, b""])
        with patch.object(peer.socket, "create_connection", return_value=sock):
            self.assertEqual(expected, peer.stats("example", 2))

    def test_fuzz_retains_resource_failure_and_growth_diagnostics(self):
        endpoint = peer.Endpoint("example", 1, False, b"example")
        for before, after in ((snapshot(), snapshot()),
                              (snapshot(), snapshot(1)),
                              ({"error": "timeout", **snapshot()}, snapshot())):
            with self.subTest(before=before, after=after), \
                    patch.object(peer, "stats", return_value=before), \
                    patch.object(peer, "settle", return_value=after):
                report = peer.run_fuzz(endpoint, 1, 0, 2)
            failed = "error" in before or after["activeHandlers"] != 0
            self.assertEqual("FAIL" if failed else "PASS", report["result"])
            if "error" not in before:
                self.assertEqual(0, report["handleGrowth"])
                self.assertEqual(0, report["managedGrowth"])

    def test_trickle_hits_absolute_budget(self):
        sock = StatsSocket([b"H", b"T"])
        # Each receive is timely, but the combined observation exhausts its budget.
        with patch.object(peer.socket, "create_connection", return_value=sock), \
                patch.object(peer.time, "monotonic", side_effect=[0, 0, 1, 6, 7, 11]):
            result = peer.stats("example", 2)
        self.assertIn("10-second budget", result["error"])
        self.assertTrue(sock.closed)

    def test_oversized_response_fails(self):
        sock = StatsSocket([b"x" * 65536, b"x"])
        with patch.object(peer.socket, "create_connection", return_value=sock):
            result = peer.stats("example", 2)
        self.assertIn("64-KiB", result["error"])
        self.assertTrue(sock.closed)

    def read_snapshot(self, value, status=b"HTTP/1.1 200 OK"):
        wire = status + b"\r\nContent-Type: application/json\r\n\r\n" + json.dumps(value).encode()
        sock = StatsSocket([wire, b""])
        with patch.object(peer.socket, "create_connection", return_value=sock):
            result = peer.stats("example", 2)
        self.assertTrue(sock.closed)
        return result

    def test_non_200_valid_json_fails(self):
        result = self.read_snapshot(snapshot(), b"HTTP/1.1 503 Service Unavailable")
        self.assertIn("status 200", result["error"])

    def test_non_object_json_fails(self):
        for value in ([], None, 7, "snapshot"):
            with self.subTest(value=value):
                self.assertIn("JSON object", self.read_snapshot(value)["error"])

    def test_missing_required_counter_fails(self):
        for name in snapshot():
            value = snapshot()
            del value[name]
            with self.subTest(name=name):
                self.assertIn(name, self.read_snapshot(value)["error"])

    def test_invalid_counter_types_and_negative_values_fail(self):
        for name in snapshot():
            for invalid in (-1, True, 1.5, "1", None):
                value = snapshot()
                value[name] = invalid
                with self.subTest(name=name, invalid=invalid):
                    self.assertIn(name, self.read_snapshot(value)["error"])

    def test_http_10_and_additional_snapshot_fields_supported(self):
        value = {**snapshot(), "completedHandlers": 40, "drainFailure": None}
        self.assertEqual(value, self.read_snapshot(value, b"HTTP/1.0 200 OK"))

    def test_malformed_json_fails_as_observation(self):
        sock = StatsSocket([b"HTTP/1.1 200 OK\r\n\r\n{", b""])
        with patch.object(peer.socket, "create_connection", return_value=sock):
            result = peer.stats("example", 2)
        self.assertIn("error", result)
        self.assertTrue(sock.closed)


if __name__ == "__main__":
    unittest.main()
