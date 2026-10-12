import copy
import json
from pathlib import Path
import tempfile
import unittest

from teardown_cost_campaign import SCENARIOS, validate, validate_recovery


class CostCampaignTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.sample = {"warmup": {"requests": 64}, "server": {"protocol": "Http1", "assemblies": [{"sha256": "a" * 64}]}, "client": {"requests": 160, "latencyCount": 160, "connectionsOpened": 2, "p99Milliseconds": 1},
                       "clientExitCode": 0, "serverExitCode": 0, "clientKilled": False, "serverKilled": False,
                       "requestRate": 32, "connectionCensus": True, "machineMeasurementSeconds": 5,
                       "derived": {"requestsPerSecond": 32, "serverCpuMicrosecondsPerRequest": 1,
                                   "serverAllocatedBytesPerRequest": 100, "openServerSocketsAfter": 0, "backgroundCpuSeconds": 0.1},
                       "connectionsBeforeLoad": {"closed": 1}, "connectionsBeforeStop": {"closed": 3},
                       "serverStop": {"exceptionSites": {"counts": {}, "types": {}, "overflow": 0}, "firstChanceExceptions": {"IO": 2}, "connections": {"opened": 3, "closed": 3, "active": 0, "instrumentSeen": True, "invalidMeasurements": 0}}}
        for name in ("AtReady", "BeforeLoad", "AfterLoad", "BeforeStop"):
            self.sample["exceptions" + name] = {"IO": 1}
            self.sample["exceptionSites" + name] = {"counts": {}, "types": {}, "overflow": 0}

    def write(self):
        for scenario in SCENARIOS:
            for engine in ("candidate", "kestrel"):
                for number in (1, 2):
                    sample = copy.deepcopy(self.sample)
                    sample.update(scenario=scenario, engine=engine, round=number)
                    p = self.root / "samples" / scenario / f"{engine}-r{number}.json"
                    p.parent.mkdir(parents=True, exist_ok=True)
                    p.write_text(json.dumps(sample))

    def test_complete_census(self):
        self.write()
        self.assertEqual(len(validate(self.root, True, 5, 2)), 28)

    def test_request_loss_rejected(self):
        self.sample["client"]["requests"] = 159
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_unclosed_connections_rejected(self):
        self.sample["serverStop"]["connections"]["active"] = 1
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_missing_instrument_rejected(self):
        self.sample["serverStop"]["connections"]["instrumentSeen"] = False
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_host_contamination_rejected(self):
        self.sample["derived"]["backgroundCpuSeconds"] = 3
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_missing_matrix_rejected(self):
        self.write()
        next((self.root / "samples").glob("*/*.json")).unlink()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_site_type_disagreement_rejected(self):
        self.sample["exceptionSitesAfterLoad"]["types"] = {"IO": 1}
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_invalid_marker_rejected(self):
        self.write()
        (self.root / "INVALID.txt").write_text("tool defect")
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_excess_zero_request_churn_rejected(self):
        self.sample["serverStop"]["connections"].update(opened=300, closed=300)
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_invalid_cpu_interval_rejected(self):
        for value in (0, -1, float("nan"), float("inf")):
            self.sample["machineMeasurementSeconds"] = value
            self.write()
            with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_stack_capture_overflow_rejected(self):
        self.sample["exceptionSitesAfterLoad"]["overflow"] = 1
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_missing_native_identity_rejected(self):
        self.sample["server"]["protocol"] = "Http3"
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_genuine_failure_is_not_accepted(self):
        self.sample["client"]["error"] = "reset"
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)


class RecoveryEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "recovery.json"
        self.report = {"verdict": [], "serverExitCode": 0, "serverKilled": False,
                       "healthy-beforeClientKilled": False, "healthy-afterClientKilled": False,
                       "phases": [{"phase": name, "client": {"requests": 1},
                                   "exceptionSites": {"counts": {}, "types": {}, "overflow": 0}}
                                  for name in ("healthy-before", "healthy-after")],
                       "stop": {"exceptionSites": {"counts": {}, "types": {}, "overflow": 0},
                                "connections": {"opened": 2, "closed": 2, "active": 0,
                                                "instrumentSeen": True, "invalidMeasurements": 0}}}

    def check(self):
        self.path.write_text(json.dumps(self.report))
        validate_recovery(self.path)

    def test_complete_recovery(self):
        self.check()

    def test_unsuccessful_server_exit_rejected(self):
        self.report["serverExitCode"] = 1
        with self.assertRaises(ValueError): self.check()

    def test_recovery_capture_overflow_rejected(self):
        self.report["stop"]["exceptionSites"]["overflow"] = 1
        with self.assertRaises(ValueError): self.check()

    def test_unclosed_recovery_lifetime_rejected(self):
        self.report["stop"]["connections"]["closed"] = 1
        with self.assertRaises(ValueError): self.check()


if __name__ == "__main__": unittest.main()
