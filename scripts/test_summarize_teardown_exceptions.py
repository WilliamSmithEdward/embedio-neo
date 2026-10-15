"""Phase accounting must retain errors and reject incomplete evidence."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from summarize_teardown_exceptions import complete_sample, difference, phases, rate, summarize


class ExceptionAccountingTests(unittest.TestCase):
    def test_rethrows_and_new_types_are_counted(self):
        self.assertEqual(difference({"IO": 7, "Cancel": 3}, {"IO": 2}), {"Cancel": 3, "IO": 5})

    def test_zero_phase(self):
        self.assertEqual(difference({"IO": 7}, {"IO": 7}), {})

    def test_missing_phase_is_unavailable(self):
        with self.assertRaisesRegex(ValueError, "missing"):
            difference(None, {})

    def test_reset_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "decreased"):
            difference({}, {"IO": 1})

    def test_invalid_count_is_rejected(self):
        for count in (-1, True, 1.2, "3"):
            with self.subTest(count=count), self.assertRaisesRegex(ValueError, "invalid"):
                difference({"IO": count}, {})

    def test_phase_boundaries_exclude_startup(self):
        result = phases({
            "exceptionsAtReady": {"IO": 2},
            "exceptionsBeforeLoad": {"IO": 5},
            "exceptionsAfterLoad": {"IO": 12},
            "exceptionsBeforeStop": {"IO": 14},
            "serverStop": {"firstChanceExceptions": {"IO": 16}},
        })
        self.assertEqual([sum(counts.values()) for counts in result.values()], [3, 7, 2, 2])

    def test_denominator_is_not_invented(self):
        self.assertEqual(rate(10, None), "unavailable")
        self.assertEqual(rate(10, 0), "unavailable")
        self.assertEqual(rate(10, True), "unavailable")
        self.assertEqual(rate(10, 5), "2.000000")

    def test_metrics_do_not_override_failed_cleanup(self):
        sample = {
            "client": {}, "clientExitCode": 0, "serverExitCode": 0,
            "clientKilled": False, "serverKilled": False,
            "derived": {"requestsPerSecond": 1, "serverCpuMicrosecondsPerRequest": 0,
                        "serverAllocatedBytesPerRequest": 0, "openServerSocketsAfter": 0},
        }
        self.assertTrue(complete_sample(sample))
        for key, value in (("clientExitCode", 1), ("serverExitCode", 1),
                           ("clientKilled", True), ("serverKilled", True)):
            with self.subTest(key=key):
                self.assertFalse(complete_sample({**sample, key: value}))
        sample["derived"]["openServerSocketsAfter"] = 1
        self.assertFalse(complete_sample(sample))

    def test_failed_sample_is_printed_and_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "samples" / "h3" / "candidate-r1.json"
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps({"error": "connection reset"}), encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertFalse(summarize(root))
            self.assertIn("connection reset", output.getvalue())

    def test_empty_campaign_fails(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            self.assertFalse(summarize(directory))


if __name__ == "__main__":
    unittest.main()
