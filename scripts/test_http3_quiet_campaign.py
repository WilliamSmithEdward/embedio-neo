"""Acceptance must reject lost samples, changed deadlines and contaminated controls."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from http3_quiet_campaign import validate


class CampaignTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.directory = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        self.environment = {"sourceRevision": "revision", "handshakeTimeout": "runtime default",
                            "burnThreads": 0, "rounds": 2, "hostIdleBaseline": {"busyCpus": 0.1},
                            "logicalProcessors": 4, "rates": [4], "phaseSeconds": 3,
                            "mixedRate": 2, "largeRate": 1}
        for key in ("runnerSha256", "embedioSha256", "kestrelCoreSha256", "kestrelQuicSha256",
                    "systemNetQuicSha256", "msquicSha256"):
            self.environment[key] = "a" * 64
        self.run = {"failureCount": 0, "serverExitCode": 0, "serverKilled": False,
                    "phases": [{"rate": 4, "started": 12, "completed": 12,
                                "failureCount": 0, "failures": [], "skippedAtCap": 0,
                                "handshake": {"count": 12}, "firstStream": {"count": 12},
                                "close": {"count": 12}, "mixed": {"failures": 0, "errors": [],
                                "small": {"count": 6}, "large": {"count": 3}},
                                "timeline": [{"hostBusyCpus": 0.3, "backgroundCpus": 0.1}]}]}

    def write(self):
        (self.directory / "environment.json").write_text(json.dumps(self.environment))
        for engine in ("embedio", "kestrel"):
            for number in (1, 2):
                (self.directory / f"{engine}-r{number}.json").write_text(json.dumps(self.run))

    def test_complete_control_passes(self):
        self.write()
        self.assertEqual(len(validate(self.directory, "revision", 0)), 4)

    def test_missing_round_is_not_accepted(self):
        self.write()
        (self.directory / "kestrel-r2.json").unlink()
        with self.assertRaises(OSError):
            validate(self.directory, "revision", 0)

    def test_incomplete_or_failed_data_is_not_accepted(self):
        original = copy.deepcopy(self.run)
        for key, value in (("completed", 11), ("skippedAtCap", 1),
                           ("failures", [{"error": "timeout"}]), ("timeline", [])):
            with self.subTest(key=key):
                self.run = copy.deepcopy(original)
                self.run["phases"][0][key] = value
                self.write()
                with self.assertRaises(ValueError):
                    validate(self.directory, "revision", 0)

    def test_missing_native_hash_or_changed_deadline_is_not_accepted(self):
        for key, value in (("msquicSha256", "not found"), ("handshakeTimeout", 30)):
            with self.subTest(key=key):
                original = self.environment[key]
                self.environment[key] = value
                self.write()
                with self.assertRaises(ValueError):
                    validate(self.directory, "revision", 0)
                self.environment[key] = original

    def test_noisy_quiet_control_is_not_accepted(self):
        self.run["phases"][0]["timeline"][0]["backgroundCpus"] = 0.6
        self.write()
        with self.assertRaises(ValueError):
            validate(self.directory, "revision", 0)

    def test_approved_ambient_repeat_retains_no_quiet_claim(self):
        self.run["phases"][0]["timeline"][0]["backgroundCpus"] = 0.6
        self.write()
        self.assertEqual(len(validate(self.directory, "revision", 0, require_quiet_phase=False)), 4)
        with self.assertRaises(ValueError):
            validate(self.directory, "revision", 0)

    def test_ambient_scope_does_not_allow_request_failure(self):
        self.run["phases"][0]["failureCount"] = 1
        self.write()
        with self.assertRaises(ValueError):
            validate(self.directory, "revision", 0, require_quiet_phase=False)

    def test_insufficient_contention_is_not_accepted(self):
        self.environment["burnThreads"] = 4
        self.write()
        with self.assertRaises(ValueError):
            validate(self.directory, "revision", 4)


if __name__ == "__main__":
    unittest.main()
