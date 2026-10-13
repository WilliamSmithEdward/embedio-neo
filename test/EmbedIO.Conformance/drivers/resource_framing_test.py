"""Offline framing controls for the shared campaign resource observation reader."""
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("resource_observation", Path(__file__).with_name("resource_observation.py"))
peer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(peer)


class Socket:
    def __init__(self, wire):
        self.chunks = iter((wire, b""))
        self.closed = False

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.closed = True

    def settimeout(self, value):
        pass

    def sendall(self, value):
        pass

    def recv(self, count):
        return next(self.chunks)


class ResourceFramingTest(unittest.TestCase):
    def setUp(self):
        self.snapshot = {"activeHandlers": 0, "handles": 4, "managedBytes": 200}
        self.body = json.dumps(self.snapshot).encode()
        self.chunk = format(len(self.body), "x").encode() + b"\r\n" + self.body + b"\r\n"

    def read(self, headers=b"", body=None):
        sock = Socket(b"HTTP/1.1 200 OK\r\n" + headers + b"\r\n" + (self.body if body is None else body))
        with patch.object(peer.socket, "create_connection", return_value=sock):
            result = peer.stats("offline", 1)
        self.assertTrue(sock.closed)
        return result

    def test_fixed_length_complete(self):
        self.assertEqual(self.snapshot, self.read(b"Content-Length: " + str(len(self.body)).encode() + b"\r\n"))

    def test_wrong_or_invalid_fixed_length(self):
        for length in (b"0", b"999", b"-1", b"invalid"):
            with self.subTest(length=length):
                self.assertIn("error", self.read(b"Content-Length: " + length + b"\r\n"))

    def test_missing_terminal_chunk_after_complete_json(self):
        self.assertIn("error", self.read(b"Transfer-Encoding: chunked\r\n", self.chunk))

    def test_truncated_chunk_or_terminator(self):
        for body in (self.chunk[:-2], self.chunk + b"0\r\n", b"FFFF\r\n" + self.body, self.chunk + b"0\r\n\r"):
            with self.subTest(body=body):
                self.assertIn("error", self.read(b"Transfer-Encoding: chunked\r\n", body))

    def test_invalid_chunk_size(self):
        for size in (b"-1", b"+1", b"", b"g"):
            with self.subTest(size=size):
                self.assertIn("error", self.read(b"Transfer-Encoding: chunked\r\n", size + b"\r\n" + self.body))

    def test_complete_chunk_extensions_and_trailer(self):
        body = format(len(self.body), "x").encode() + b";test=yes\r\n" + self.body + b"\r\n0\r\nX-Witness: yes\r\n\r\n"
        self.assertEqual(self.snapshot, self.read(b"Transfer-Encoding: chunked\r\n", body))

    def test_duplicate_or_conflicting_framing(self):
        for headers in (b"Content-Length: 0\r\nContent-Length: 0\r\n",
                        b"Transfer-Encoding: chunked\r\nContent-Length: 0\r\n",
                        b"Transfer-Encoding: gzip\r\n",
                        b"Transfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n"):
            with self.subTest(headers=headers):
                self.assertIn("error", self.read(headers))

    def test_reported_observation_error_is_not_a_zero_snapshot(self):
        self.assertIn("error", self.read(body=json.dumps({**self.snapshot, "error": "counter read failed"}).encode()))

    def test_socket_failure_returns_no_fabricated_counters(self):
        with patch.object(peer.socket, "create_connection", side_effect=OSError("offline")):
            self.assertEqual({"error": "OSError: offline"}, peer.stats("offline", 1))


if __name__ == "__main__":
    unittest.main()
