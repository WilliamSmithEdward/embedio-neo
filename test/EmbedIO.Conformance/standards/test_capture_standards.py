"""Offline controls for bounded primary-source standards capture."""
import contextlib
import importlib.metadata
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
import urllib.request
from unittest.mock import Mock, patch
from defusedxml.common import DTDForbidden, EntitiesForbidden

SPEC = importlib.util.spec_from_file_location("standards_capture", Path(__file__).with_name("capture_standards.py"))
if SPEC is None or SPEC.loader is None:
    raise ImportError("Missing standards capture module")
CAPTURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CAPTURE)
URL = "https://www.rfc-editor.org/rfc/rfc9000.json"
XML = b'<rfc-index xmlns="https://www.rfc-editor.org/rfc-index"><rfc-entry><doc-id>RFC9000</doc-id></rfc-entry></rfc-index>'


class Response(io.BytesIO):
    def __init__(self, body, url=URL):
        super().__init__(body)
        self.status = 200
        self.url = url
        self.requested = []

    def geturl(self):
        return self.url

    def read(self, size=-1):
        self.requested.append(size)
        return super().read(size)


class CaptureTests(unittest.TestCase):
    def test_standards_lock_matches_reviewed_parser_runtime(self):
        root = Path(__file__).resolve().parents[3]
        lock = (root / ".github/requirements/standards-capture.txt").read_text()
        shared = (root / ".github/requirements/prettycsharp.txt").read_text()
        requirement = "defusedxml==" + importlib.metadata.version("defusedxml")
        self.assertIn(requirement, lock)
        self.assertIn(requirement, shared)
        self.assertIn("--hash=sha256:a352e7e428770286cc899e2542b6cdaedb2b4953ff269a210103ec58f6198a61", lock)

    def test_primary_https_origins_and_encoded_paths(self):
        for url in (URL, "https://www.iana.org/assignments/http-fields/http-fields.xml",
                    "https://datatracker.ietf.org/doc/draft-ietf-webtrans-http3/doc.json",
                    "https://rfc-editor.org:443/rfc/rfc9000.json"):
            with self.subTest(url=url):
                self.assertEqual(CAPTURE.source_url(url), url)

    def test_non_primary_or_credentialed_targets_fail_before_open(self):
        values = ("http://www.rfc-editor.org/rfc/rfc9000.json", "file:///private.xml",
                  "https://127.0.0.1/private", "https://www.rfc-editor.org.evil.invalid/private",
                  "https://www.rfc-editor.org@evil.invalid/private", "https://user:password@www.rfc-editor.org/x",
                  "https://www.rfc-editor.org:444/x", "https://www.rfc-editor.org/x#fragment",
                  "https://www.rfc-editor.org/\r\nx", "//www.rfc-editor.org/x", "https://www.rfc-editor.org:invalid/x", None)
        with patch.object(CAPTURE.urllib.request, "build_opener") as opener:
            for url in values:
                with self.subTest(url=url), self.assertRaises((TypeError, ValueError)):
                    CAPTURE.fetch(url)
            opener.assert_not_called()

    def test_redirects_validate_before_opening_next_target(self):
        request = urllib.request.Request(URL)
        handler = CAPTURE.SourceRedirects()
        for target in ("file:///private.xml", "http://www.iana.org/x", "https://external.invalid/x"):
            with self.subTest(target=target), patch.object(urllib.request.HTTPRedirectHandler, "redirect_request") as parent:
                with self.assertRaises(ValueError):
                    handler.redirect_request(request, None, 302, "Found", {}, target)
                parent.assert_not_called()
        target = "https://www.rfc-editor.org/rfc/rfc9000.json?format=json"
        forwarded = handler.redirect_request(request, None, 302, "Found", {}, target)
        self.assertEqual(forwarded.full_url, target)

    def test_bounded_fetch_preserves_bytes_status_and_final_url(self):
        for length in (0, 16, 17):
            response = Response(b"x" * length)
            opener = Mock()
            opener.open.return_value = response
            with self.subTest(length=length), patch.object(CAPTURE, "MAX_RESPONSE_BYTES", 16), \
                    patch.object(CAPTURE.urllib.request, "build_opener", return_value=opener):
                if length > 16:
                    with self.assertRaisesRegex(ValueError, "byte limit"):
                        CAPTURE.fetch(URL)
                else:
                    self.assertEqual(CAPTURE.fetch(URL), (URL, 200, b"x" * length))
            self.assertEqual(response.requested, [17])
            self.assertTrue(response.closed)
            request = opener.open.call_args.args[0]
            self.assertEqual(request.full_url, URL)
            self.assertEqual(opener.open.call_args.kwargs, {"timeout": 120})

    def test_unapproved_final_url_is_rejected_without_reading(self):
        response = Response(b"private", "file:///private.xml")
        opener = Mock()
        opener.open.return_value = response
        with patch.object(CAPTURE.urllib.request, "build_opener", return_value=opener), self.assertRaises(ValueError):
            CAPTURE.fetch(URL)
        self.assertEqual(response.requested, [])
        self.assertTrue(response.closed)

    def test_valid_index_retains_namespaces(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "index.xml"
            path.write_bytes(XML)
            rows = CAPTURE.rfc_index_entries(path)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0].findtext("r:doc-id", namespaces={"r": "https://www.rfc-editor.org/rfc-index"}), "RFC9000")

    def test_dtd_internal_and_external_entities_are_rejected(self):
        payloads = (
            b'<!DOCTYPE rfc-index [<!ENTITY value "expanded">]><rfc-index>&value;</rfc-index>',
            b'<!DOCTYPE rfc-index SYSTEM "file:///private.xml"><rfc-index/>',
            b'<!DOCTYPE rfc-index [<!ENTITY value SYSTEM "https://external.invalid/x">]><rfc-index>&value;</rfc-index>',
            b'<!DOCTYPE rfc-index><rfc-index/>')
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "index.xml"
            for data in payloads:
                path.write_bytes(data)
                with self.subTest(data=data), self.assertRaises((DTDForbidden, EntitiesForbidden)):
                    CAPTURE.rfc_index_entries(path)

    def test_malformed_and_oversized_xml_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "index.xml"
            path.write_bytes(b"<broken>")
            with self.assertRaises(CAPTURE.SafeET.ParseError):
                CAPTURE.rfc_index_entries(path)
            path.write_bytes(XML)
            with patch.object(CAPTURE, "MAX_RESPONSE_BYTES", len(XML) - 1), self.assertRaisesRegex(ValueError, "byte limit"):
                CAPTURE.rfc_index_entries(path)

    def test_failed_fetch_does_not_write_capture(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with patch.object(CAPTURE, "fetch", side_effect=ValueError("refused")), self.assertRaises(ValueError):
                CAPTURE.record(root, "rfc/rfc9000.json", URL)
            self.assertFalse((root / "rfc/rfc9000.json").exists())

    def test_update_graph_count_is_bounded_without_fabricated_manifest(self):
        calls = []
        def record(root, name, url):
            calls.append(name)
            number = int(name.split("rfc")[-1].removesuffix(".json"))
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps({"updated_by": [f"RFC{number + 1}"]}))
            return {"file": name}
        with tempfile.TemporaryDirectory() as temporary, patch.object(CAPTURE, "SEED_RFCS", [1]), \
                patch.object(CAPTURE, "MAX_RFCS", 2), patch.object(CAPTURE, "record", side_effect=record), \
                patch.object(sys, "argv", ["capture", temporary]), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(ValueError, "document limit"):
                CAPTURE.main()
            self.assertEqual(calls, ["rfc/rfc1.json", "rfc/rfc2.json"])
            self.assertFalse((Path(temporary) / "manifest.json").exists())


if __name__ == "__main__":
    unittest.main()
