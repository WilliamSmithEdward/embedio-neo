"""Check sample accounting and zero-valued metrics in benchmark comparisons."""
import contextlib
import io
import json
import math
from pathlib import Path
import tempfile
from unittest.mock import patch
import unittest

from compare_load_benchmark import main, median, noise, ratio_magnitude, samples, spread, valid_sample


def sample(rate=100, cpu=2, allocation=10, error=None):
    return {"error": error, "derived": {
        "requestsPerSecond": rate,
        "serverCpuMicrosecondsPerRequest": cpu,
        "serverAllocatedBytesPerRequest": allocation,
    }}


class ComparisonTests(unittest.TestCase):
    def test_failed_samples_are_not_measurements(self):
        self.assertEqual(median([sample(), sample(rate=900, error="timeout")], "requestsPerSecond"), (100, 1))

    def test_partial_sample_is_excluded_from_every_metric(self):
        partial = sample()
        del partial["derived"]["serverAllocatedBytesPerRequest"]
        for metric in sample()["derived"]:
            self.assertEqual(median([partial], metric), (None, 0))

    def test_nonfinite_and_invalid_measurements_are_rejected(self):
        for value in (math.nan, math.inf, -1, True, "100"):
            for field in ("rate", "cpu", "allocation"):
                with self.subTest(value=value, field=field):
                    self.assertFalse(valid_sample(sample(**{field: value})))
        self.assertFalse(valid_sample(sample(rate=0)))

    def test_zero_cost_is_a_valid_measurement(self):
        self.assertTrue(valid_sample(sample(cpu=0, allocation=0)))
        self.assertEqual(ratio_magnitude(0, 10), math.inf)
        self.assertEqual(ratio_magnitude(0, 0), 1)

    def test_spread_includes_zero_cost_samples(self):
        self.assertEqual(spread([sample(allocation=0), sample(allocation=10)], "serverAllocatedBytesPerRequest"), math.inf)
        self.assertEqual(spread([sample(allocation=0), sample(allocation=0)], "serverAllocatedBytesPerRequest"), 1)

    def test_median_and_ratio_are_symmetric(self):
        self.assertEqual(median([sample(rate=100), sample(rate=200), sample(rate=300)], "requestsPerSecond"), (200, 3))
        self.assertEqual(ratio_magnitude(100, 200), 2)
        self.assertEqual(ratio_magnitude(200, 100), 2)


    def test_rejected_runs_cannot_supply_comparison_or_noise_samples(self):
        for marker in ("INVALID.txt", "ABORTED.txt"):
            with self.subTest(marker=marker), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                scenario = root / "samples/ordinary"
                scenario.mkdir(parents=True)
                for engine in ("baseline", "candidate"):
                    (scenario / f"{engine}-r1.json").write_text(json.dumps(sample()))
                (root / marker).write_text("foreign workload invalidated this run")
                with self.assertRaisesRegex(ValueError, marker):
                    samples(root)
                with self.assertRaisesRegex(ValueError, marker):
                    noise([root])

    def test_cli_reports_rejected_comparison_and_aa_runs_without_claims(self):
        for rejected in ("comparison", "aa"):
            with self.subTest(rejected=rejected), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                comparison, aa = root / "comparison", root / "aa"
                comparison.mkdir()
                aa.mkdir()
                (root / rejected / "ABORTED.txt").write_text("bounded run aborted")
                output, errors = io.StringIO(), io.StringIO()
                with patch("sys.argv", ["compare_load_benchmark.py", str(comparison), "--aa", str(aa)]), contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
                    with self.assertRaises(SystemExit) as caught:
                        main()
                self.assertEqual(caught.exception.code, 2)
                self.assertIn("ABORTED.txt", errors.getvalue())
                self.assertEqual(output.getvalue(), "")

if __name__ == "__main__":
    unittest.main()
