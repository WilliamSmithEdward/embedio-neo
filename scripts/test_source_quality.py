"""Regression cases proving source-quality gates fail closed."""
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from check_source_quality import check_messages, check_pretty, expected_format, scan_failed


class SourceQualityTests(unittest.TestCase):
    def test_unknown_project_shard_fails_closed(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "src").mkdir()
            (root / "src/Valid.csproj").write_text("<Project />", encoding="utf-8")
            with patch("check_source_quality.ROOT", root):
                with self.assertRaises(ValueError):
                    check_messages(root, ["src/Missing.csproj"])

    def test_missing_editorconfig_bom_fails(self):
        import prettycsharp
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "src").mkdir()
            (root / ".editorconfig").write_text("root = true\n[*.cs]\ncharset = utf-8-bom\n", encoding="utf-8")
            source = root / "src" / "C.cs"
            source.write_text(prettycsharp.format_source("class C {}\n"), encoding="utf-8")
            with patch("check_source_quality.ROOT", root):
                self.assertFalse(check_pretty(root))
                source.write_bytes(b"\xef\xbb\xbf" + source.read_bytes())
                self.assertTrue(check_pretty(root))

    def test_bad_formatting_fails_and_does_not_modify_source(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "src").mkdir()
            source = root / "src" / "C.cs"
            source.write_text("class C{int x=1;}\n", encoding="utf-8")
            original = source.read_bytes()
            with patch("check_source_quality.ROOT", root):
                self.assertFalse(check_pretty(root))
            self.assertEqual(source.read_bytes(), original)
            self.assertTrue((root / "pretty.diff").read_text())

    def test_informational_message_fails(self):
        for code in ("IDE0090", "CA1844", "CS9236"):
            self.assertTrue(scan_failed(2, f"src/C.cs(1,2): info {code}: example\nFormat complete in 1ms."))

    def test_hidden_only_does_not_fail(self):
        self.assertFalse(scan_failed(2, "src/C.cs(1,2): hidden IDE0011: braces\nFormat complete in 1ms."))

    def test_diagnostics_without_source_locations_fail(self):
        for diagnostic in (
            "warning AD0001: Analyzer threw an exception",
            "CSC : warning AD0001: Analyzer threw an exception",
            "error CS0006: Metadata file could not be found",
            "info CA1707: Remove underscores",
            "  info CUSTOM_RULE: Custom informational diagnostic",
            "src/C.cs(1): warning CS0168: Unused variable",
        ):
            with self.subTest(diagnostic=diagnostic):
                self.assertTrue(scan_failed(0, diagnostic + "\nFormat complete in 1ms."))

    def test_errors_and_incomplete_scans_fail(self):
        for output in ("", "Format complete in 1ms.\nRequired references did not load",
                       "src/C.cs(1,2): warning CA1001: example\nFormat complete in 1ms."):
            self.assertTrue(scan_failed(0, output))
        self.assertTrue(scan_failed(1, "Format complete in 1ms."))

    def test_workaround_is_path_and_expression_specific(self):
        source = "i* ExactAllocationLimit; j* ExactAllocationLimit;"
        self.assertEqual(expected_format("src/Other.cs", source), source)
        self.assertEqual(expected_format("src/EmbedIO/WebSockets/Internal/WebSocketFrameStream.cs", source),
                         "i * ExactAllocationLimit; j* ExactAllocationLimit;")

    def test_unrelated_attribute_is_not_rewritten(self):
        source = '[SuppressMessage("Usage", "CA2213", Justification = "other")]'
        self.assertEqual(expected_format("src/EmbedIO/Caching/OutputCacheCapture.cs", source), source)

    def test_owned_capture_attribute_layout_is_preserved(self):
        reason = "Borrowed parent reference; disposing it here would discard the buffer before cache publication."
        source = ('[System.Diagnostics.CodeAnalysis.SuppressMessage(\n'
                  '    "Usage",\n    "CA2213:Disposable fields should be disposed",\n'
                  f'    Justification = "{reason}"\n)]')
        result = expected_format("src/EmbedIO/Caching/OutputCacheCapture.cs", source)
        self.assertNotIn("\n", result)
        self.assertIn(reason, result)
        self.assertEqual(expected_format("src/Other.cs", source), source)


if __name__ == "__main__":
    unittest.main()
