"""Offline regression tests for campaign result integrity; no sockets or campaigns.

Run with the same pinned Python environment as h2_campaign.py.
"""
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("h2_campaign_oracles", Path(__file__).with_name("h2_campaign.py"))
peer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(peer)


class ClosedPeer:
    def __init__(self):
        self.closed = False

    def setsockopt(self, *args):
        pass

    def sendall(self, data):
        pass

    def recv(self, count):
        return b""

    def close(self):
        self.closed = True


class PlainRandom:
    def __init__(self, method):
        self.method = method
        self.values = iter((0.9, 0.9, 0.9, 0.1, 0.9, 0.1))

    def random(self):
        return next(self.values)

    def randint(self, lower, upper):
        return lower

    def choice(self, choices):
        return self.method


class WrongBodyClient:
    def __init__(self, endpoint, settings=None):
        self.sock = ClosedPeer()
        self.conn = SimpleNamespace(increment_flow_control_window=lambda value: None)
        self.streams = {}
        self.trace = []
        self.terminated = None

    def request(self, method, path, headers=()):
        self.streams[1] = {"status": 200, "body": b"wrong", "reset": None}
        return 1

    def update_settings(self, settings):
        pass

    def flush(self):
        pass

    def wait(self, stream_ids, timeout=None):
        pass

    def close(self):
        self.closed = True


class CampaignOracleTests(unittest.TestCase):
    def test_shrink_requires_observed_ack(self):
        self.assert_missing_ack_fails("settings-shrink-ordering")

    def test_pair_requires_observed_acks(self):
        self.assert_missing_ack_fails("settings-pair-ordering")

    def assert_missing_ack_fails(self, identifier):
        function = next(item[3] for item in peer.CASES if item[0] == identifier)
        sock = ClosedPeer()
        with patch.object(peer, "raw_connection", return_value=sock), patch.object(peer, "raw_headers_block", return_value=b""), \
                patch.object(peer.select, "select", return_value=([sock], [], [])):
            with self.assertRaisesRegex(AssertionError, "acknowledged"):
                function(SimpleNamespace(cleanup=None))

    def test_unavailable_requested_stats_cannot_pass(self):
        with patch.object(peer, "_stats", side_effect=OSError("stats unavailable")):
            result = peer.run_fuzz(SimpleNamespace(host="unused"), 1, 0, 1234)
        self.assertEqual("FAIL", result["result"])
        self.assertEqual("resource observations unavailable", result["error"])

    def test_plain_get_body_is_checked(self):
        self.assert_wrong_body_fails(b"GET")

    def test_plain_head_body_is_checked(self):
        self.assert_wrong_body_fails(b"HEAD")

    def assert_wrong_body_fails(self, method):
        with patch.object(peer.random, "Random", return_value=PlainRandom(method)), \
                patch.object(peer, "Client", WrongBodyClient), patch.object(peer, "healthy", return_value=None):
            result = peer.run_fuzz(SimpleNamespace(host="unused"), 1, 1, None)
        self.assertEqual("FAIL", result["result"])
        self.assertIn("body=", result["error"])

    def test_case_failure_closes_owned_socket(self):
        endpoint = peer.Endpoint("unused", 1234, False, b"unused")
        sock = ClosedPeer()
        cases = []

        @peer.case(cases, "synthetic", "ownership", "test")
        def failure(target):
            target.connect()
            raise AssertionError("synthetic failure")

        with patch.object(peer.socket, "create_connection", return_value=sock):
            with self.assertRaisesRegex(AssertionError, "synthetic failure"):
                cases[0][3](endpoint)
        self.assertTrue(sock.closed)
        self.assertIsNone(endpoint.cleanup)

    def test_constructor_failure_closes_owned_socket(self):
        sock = ClosedPeer()
        endpoint = SimpleNamespace(connect=lambda: sock)
        with patch.object(peer.h2.connection, "H2Connection", side_effect=RuntimeError("synthetic init")):
            with self.assertRaisesRegex(RuntimeError, "synthetic init"):
                peer.Client(endpoint)
        self.assertTrue(sock.closed)

    def test_health_failure_closes_owned_socket(self):
        client = WrongBodyClient(None)
        with patch.object(peer, "Client", return_value=client), patch.object(client, "request", side_effect=RuntimeError("synthetic request")):
            result = peer.healthy(SimpleNamespace())
        self.assertIn("synthetic request", result)
        self.assertTrue(client.sock.closed)

    def test_goaway_trace_ignores_debug_bytes_when_decoding_error(self):
        log = []
        frame = peer.hf.GoAwayFrame(0, last_stream_id=3, error_code=1, additional_data=b"debug bytes")
        peer.FrameTrace("<-", log).feed(frame.serialize())
        self.assertIn(" code=1", log[0])
        self.assertNotIn("code=" + str(int.from_bytes(b"ytes", "big")), log[0])


if __name__ == "__main__":
    unittest.main()
