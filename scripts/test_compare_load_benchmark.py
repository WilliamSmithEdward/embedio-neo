"""Check sample accounting and zero-valued metrics in benchmark comparisons."""
import math
import unittest

from compare_load_benchmark import median, ratio_magnitude, spread, valid_sample


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


if __name__ == "__main__":
    unittest.main()
