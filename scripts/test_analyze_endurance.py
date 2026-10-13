"""Synthetic endurance report controls; no server or workload is started."""
from contextlib import redirect_stdout, redirect_stderr
import io
import json
from pathlib import Path
import runpy
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name("analyze_endurance.py")
TEMP_ROOT = SCRIPT.parents[1] / "TestResults" / "endurance-report-tests"
TEMP_ROOT.mkdir(parents=True, exist_ok=True)

class EnduranceReportTests(unittest.TestCase):
    def run_report(self, root):
        with patch.object(sys, "argv", [str(SCRIPT), str(root)]), redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            runpy.run_path(str(SCRIPT), run_name="__main__")

    def fixture(self, root):
        (root / "steps.jsonl").write_text("")
        (root / "server-samples.jsonl").write_text("")
        (root / "environment.json").write_text("{}")
        (root / "clients").mkdir()

    def test_latency_quarters_follow_timestamps_across_cycle_and_step_names(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as directory:
            root = Path(directory)
            self.fixture(root)
            for filename, offset, latency in (("3001-mix", 0, 1), ("0004-mix", 4, 9)):
                rows = [{"utc": f"2026-10-13T00:00:0{offset+i}Z", "workloads": {
                    "small-http1": {"ok": 1, "p99": latency, "max": latency}}} for i in range(4)]
                (root / "clients" / (filename + ".intervals.jsonl")).write_text("\n".join(json.dumps(r) for r in rows))
            self.run_report(root)
            self.assertIn("| small-http1 | 8 | 5.00 | 1.00 | 9.00 |", (root / "endurance-summary.md").read_text())

    def test_rejected_runs_cannot_generate_retention_or_latency_claims(self):
        for marker in ("INVALID.txt", "ABORTED.txt"):
            with self.subTest(marker=marker), tempfile.TemporaryDirectory(dir=TEMP_ROOT) as directory:
                root = Path(directory)
                self.fixture(root)
                (root / marker).write_text("foreign workload")
                with self.assertRaises(SystemExit) as error:
                    self.run_report(root)
                self.assertEqual(error.exception.code, 2)
                self.assertFalse((root / "endurance-summary.md").exists())

if __name__ == "__main__":
    unittest.main()
