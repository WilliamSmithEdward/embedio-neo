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

    def test_native_census_series_and_handle_types(self):
        def snapshot(minute, handles, events, anonymous_mib, malloc_mib):
            return {
                "utc": f"2026-10-24T00:{minute:02d}:00Z", "managedHeapBytes": 4 << 20, "gcCommittedBytes": 10 << 20,
                "workingSetBytes": 100 << 20, "privateBytes": 200 << 20, "handles": handles, "threads": 20,
                "threadPoolThreads": 5, "timers": 5, "inFlightRequests": 0, "webSocketsActive": 0, "serverTcp": {},
                "availablePhysicalBytes": -1, "staleGenerationRequests": 0,
                "handleTypes": {"handles": handles, "types": {"Event": events, "File": 30}},
                "linuxMemory": {
                    "status": {"RssAnon": anonymous_mib << 20, "RssFile": 40 << 20},
                    "smapsRollup": {"Private_Dirty": anonymous_mib << 20},
                    "malloc": {"allocatedBytes": malloc_mib << 20, "mmappedBytes": 1 << 20, "freeBytes": 2 << 20},
                },
            }

        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as directory:
            root = Path(directory)
            self.fixture(root)
            steps = [{"index": 1, "step": "idle:60", "quiesced": snapshot(0, 90, 40, 50, 5)},
                     {"index": 2, "step": "load:steady:60", "quiesced": snapshot(1, 95, 45, 52, 6)}]
            steps += [{"index": 3 + cycle, "step": "restart:1", "cycles": [
                {"cycle": 1, "quiesced": snapshot(2 + cycle, 100 + 2 * cycle, 50 + 2 * cycle, 60 + cycle, 7 + cycle)}]} for cycle in range(4)]
            (root / "steps.jsonl").write_text("\n".join(json.dumps(step) for step in steps))
            self.run_report(root)
            summary = (root / "endurance-summary.md").read_text()
            self.assertIn("## Like-for-like series: every `restart:1 c1` snapshot", summary)
            self.assertIn("| Handles | 100, 102, 104, 106 | +2, +2, +2 | +6 |", summary)
            self.assertIn("| Resident anonymous MiB | 60.0, 61.0, 62.0, 63.0 | +1.0, +1.0, +1.0 | +3.0 |", summary)
            self.assertIn("| malloc in use MiB | 8.0, 9.0, 10.0, 11.0 | +1.0, +1.0, +1.0 | +3.0 |", summary)
            self.assertIn("| Event | 50 | 56 | +6 |", summary)
            self.assertIn("| File | 30 | 30 | +0 |", summary)

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
