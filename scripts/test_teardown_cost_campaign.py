import copy
import json
import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import contextlib
import io
import subprocess

from teardown_cost_campaign import SCENARIOS, validate, validate_recovery, validate_sites, validate_lifetime, record_fault_failure, wait_with_progress, UnsettledLifetimeObserver, validate_expected_core


class CostCampaignTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.sample = {"warmup": {"requests": 64}, "server": {"protocol": "Http1", "exceptionCensus": True, "assemblies": [{"sha256": "a" * 64}]}, "client": {"requests": 160, "latencyCount": 160, "connectionsOpened": 2, "p99Milliseconds": 1},
                       "clientExitCode": 0, "serverExitCode": 0, "clientKilled": False, "serverKilled": False,
                       "requestRate": 32, "connectionCensus": True, "exceptionCensus": True, "machineMeasurementSeconds": 5,
                       "derived": {"requestsPerSecond": 32, "serverCpuMicrosecondsPerRequest": 1,
                                   "serverAllocatedBytesPerRequest": 100, "openServerSocketsAfter": 0, "backgroundCpuSeconds": 0.1},
                       "connectionsBeforeLoad": {"closed": 1}, "connectionsBeforeStop": {"closed": 3},
                       "serverStop": {"observerComplete": True, "exceptionSites": {"schema": "throw-site-v2", "counts": {}, "types": {}, "siteTypes": {}, "examples": {}, "overflow": 0}, "firstChanceExceptions": {"IO": 2}, "connections": {"opened": 3, "closed": 3, "active": 0, "instrumentSeen": True, "available": True, "source": "server-meter", "invalidMeasurements": 0}}}
        for name in ("AtReady", "BeforeLoad", "AfterLoad", "BeforeStop"):
            self.sample["exceptions" + name] = {"IO": 1}
            self.sample["exceptionSites" + name] = {"schema": "throw-site-v2", "counts": {}, "types": {}, "siteTypes": {}, "examples": {}, "overflow": 0}

    def write(self):
        for scenario in SCENARIOS:
            for engine in ("candidate", "kestrel"):
                for number in (1, 2):
                    sample = copy.deepcopy(self.sample)
                    sample.update(scenario=scenario, engine=engine, round=number)
                    sample["server"]["engine"] = "kestrel" if engine == "kestrel" else "embedio"
                    sample["server"].setdefault("controlHardCleanup", False)
                    if engine == "kestrel" and sample["serverStop"]["connections"]["source"] == "server-meter":
                        sample["serverStop"]["connections"]["source"] = "kestrel-connection-delegate"
                        c = sample["serverStop"]["connections"]
                        c["reportedMeter"] = {"opened": c["opened"], "closed": c["closed"], "active": c["active"],
                                              "invalidMeasurements": 0, "completed": True, "delegateActiveAtCompletion": 0, "activeAtCompletion": c["active"]}
                    p = self.root / "samples" / scenario / f"{engine}-r{number}.json"
                    p.parent.mkdir(parents=True, exist_ok=True)
                    p.write_text(json.dumps(sample))

    def test_complete_census(self):
        self.write()
        self.assertEqual(len(validate(self.root, True, 5, 2)), 28)

    def test_cost_observers_disabled(self):
        self.sample["connectionCensus"] = False
        self.sample["exceptionCensus"] = False
        self.sample["server"]["exceptionCensus"] = False
        self.write()
        rows = validate(self.root, False, 5, 2)
        self.assertTrue(all(row["exceptions"] is None for row in rows))

    def test_server_observer_disagreement_rejected(self):
        self.sample["server"]["exceptionCensus"] = False
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_wrong_actual_engine_rejected(self):
        self.write()
        p = next((self.root / "samples").glob("*/kestrel-r1.json"))
        sample = json.loads(p.read_text())
        sample["server"]["engine"] = "embedio"
        p.write_text(json.dumps(sample))
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_experimental_cleanup_not_a_cost_baseline(self):
        self.sample["server"]["controlHardCleanup"] = True
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_wrong_primary_observer_rejected(self):
        self.sample["serverStop"]["connections"]["source"] = "kestrel-connection-delegate"
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_unsettled_lifetime_observer_rejected(self):
        self.sample["serverStop"]["observerComplete"] = False
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_request_loss_rejected(self):
        self.sample["client"]["requests"] = 159
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_unclosed_connections_rejected(self):
        self.sample["serverStop"]["connections"]["active"] = 1
        self.write()
        with self.assertRaises(ValueError): validate(self.root, True, 5, 2)

    def test_missing_instrument_rejected(self):
        self.sample["serverStop"]["connections"]["available"] = False
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


class WitnessEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.census = {"source": "kestrel-connection-delegate", "available": True, "opened": 3, "closed": 3,
                       "active": 0, "invalidMeasurements": 0, "instrumentSeen": True,
                       "reportedMeter": {"opened": 3, "closed": 2, "active": 1, "invalidMeasurements": 0,
                                         "completed": True, "delegateActiveAtCompletion": 0, "activeAtCompletion": 1}}

    def test_unexplained_residual_rejected(self):
        with self.assertRaises(ValueError): validate_lifetime(self.census, self.census["source"])

    def test_live_residual_rejected(self):
        self.census["reportedMeter"].update(completed=False, delegateActiveAtCompletion=1)
        with self.assertRaises(ValueError): validate_lifetime(self.census, self.census["source"])

    def test_invalid_witness_rejected(self):
        self.census["reportedMeter"].update(invalidMeasurements=1, delegateActiveAtCompletion=1)
        with self.assertRaises(ValueError): validate_lifetime(self.census, self.census["source"])

    def test_ended_coverage_is_explicit(self):
        self.census["reportedMeter"]["delegateActiveAtCompletion"] = 1
        self.assertIn("native release unmeasured", validate_lifetime(self.census, self.census["source"]))

    def test_residual_exceeds_coverage_gap_rejected(self):
        self.census["reportedMeter"].update(closed=1, active=2, activeAtCompletion=2, delegateActiveAtCompletion=1)
        with self.assertRaises(ValueError): validate_lifetime(self.census, self.census["source"])

    def test_contradictory_counter_tuple_rejected(self):
        self.census["reportedMeter"]["active"] = 2
        with self.assertRaises(ValueError): validate_lifetime(self.census, self.census["source"])

    def test_balanced_witness(self):
        self.census["reportedMeter"].update(closed=3, active=0, activeAtCompletion=0)
        self.assertEqual(validate_lifetime(self.census, self.census["source"]), "balanced")


class CoreIdentityTests(unittest.TestCase):
    def test_matching_loaded_identity(self):
        validate_expected_core({"assemblies": [{"name": "EmbedIO", "sha256": "a" * 64}]}, "A" * 64)

    def test_unexpected_loaded_identity_rejected(self):
        with self.assertRaises(ValueError):
            validate_expected_core({"assemblies": [{"name": "EmbedIO", "sha256": "b" * 64}]}, "a" * 64)

    def test_missing_or_ambiguous_core_rejected(self):
        for assemblies in ([], [{"name": "EmbedIO", "sha256": "a" * 64}] * 2):
            with self.assertRaises(ValueError):
                validate_expected_core({"assemblies": assemblies}, "a" * 64)


class SiteEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.site = {"schema": "throw-site-v2", "counts": {"site": 1}, "types": {"IO": 1},
                     "siteTypes": {"site": "IO"}, "overflow": 0,
                     "examples": {"site": {"text": "stack", "sha256": hashlib.sha256(b"stack").hexdigest(),
                                           "originalCharacters": 5, "truncated": False}}}

    def test_typed_site_identity(self):
        validate_sites(self.site)

    def test_equal_totals_with_wrong_type_rejected(self):
        self.site["siteTypes"]["site"] = "different"
        with self.assertRaises(ValueError): validate_sites(self.site)

    def test_incomplete_example_rejected(self):
        self.site["examples"].clear()
        with self.assertRaises(ValueError): validate_sites(self.site)

    def test_corrupted_example_rejected(self):
        self.site["examples"]["site"]["text"] = "other"
        with self.assertRaises(ValueError): validate_sites(self.site)

    def test_explicit_truncated_example(self):
        self.site["examples"]["site"].update(originalCharacters=20000, truncated=True, sha256=hashlib.sha256(("stack" + "x" * 19995).encode()).hexdigest())
        validate_sites(self.site)


class RecoveryEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "recovery.json"
        self.report = {"engine": "embedio", "server": {"exceptionCensus": True, "engine": "embedio", "controlHardCleanup": False},
                       "healthy-beforeClientExitCode": 0, "healthy-afterClientExitCode": 0, "verdict": [], "serverExitCode": 0, "serverKilled": False,
                       "healthy-beforeClientKilled": False, "healthy-afterClientKilled": False,
                       "phases": [{"phase": name, "client": {"requests": 1, "latencyCount": 1},
                                   "exceptionSites": {"schema": "throw-site-v2", "counts": {}, "types": {}, "siteTypes": {}, "examples": {}, "overflow": 0}}
                                  for name in ("healthy-before", "healthy-after")],
                       "stop": {"observerComplete": True, "exceptionSites": {"schema": "throw-site-v2", "counts": {}, "types": {}, "siteTypes": {}, "examples": {}, "overflow": 0},
                                "connections": {"opened": 2, "closed": 2, "active": 0,
                                                "instrumentSeen": True, "available": True, "source": "server-meter", "invalidMeasurements": 0}}}

    def check(self):
        self.path.write_text(json.dumps(self.report))
        validate_recovery(self.path)

    def test_complete_recovery(self):
        self.check()

    def test_wrong_recovery_engine_rejected(self):
        self.report["server"]["engine"] = "kestrel"
        with self.assertRaises(ValueError): self.check()

    def test_experimental_cleanup_not_required_recovery(self):
        self.report["server"]["controlHardCleanup"] = True
        with self.assertRaises(ValueError): self.check()

    def test_unsuccessful_client_exit_rejected(self):
        self.report["healthy-beforeClientExitCode"] = 137
        with self.assertRaises(ValueError): self.check()

    def test_missing_healthy_latency_rejected(self):
        self.report["phases"][0]["client"]["latencyCount"] = 0
        with self.assertRaises(ValueError): self.check()

    def test_unsuccessful_server_exit_rejected(self):
        self.report["serverExitCode"] = 1
        with self.assertRaises(ValueError): self.check()

    def test_recovery_capture_overflow_rejected(self):
        self.report["stop"]["exceptionSites"]["overflow"] = 1
        with self.assertRaises(ValueError): self.check()

    def test_false_settle_marker_without_pending_work_is_not_exempt(self):
        self.report["stop"]["observerComplete"] = False
        with self.assertRaises(ValueError) as caught: self.check()
        self.assertNotIsInstance(caught.exception, UnsettledLifetimeObserver)

    def test_malformed_unsettled_tuple_is_not_exempt(self):
        self.report["stop"]["observerComplete"] = False
        self.report["stop"]["connections"].update(closed=1, active=0)
        with self.assertRaises(ValueError) as caught: self.check()
        self.assertNotIsInstance(caught.exception, UnsettledLifetimeObserver)

    def test_unclosed_recovery_lifetime_rejected(self):
        self.report["stop"]["connections"]["closed"] = 1
        with self.assertRaises(ValueError): self.check()


class ProgressDeadlineTests(unittest.TestCase):
    def test_progress_does_not_extend_original_deadline(self):
        child = Mock(args=["fake"])
        child.wait.side_effect = subprocess.TimeoutExpired(child.args, 30)
        with tempfile.TemporaryDirectory() as output, contextlib.redirect_stdout(io.StringIO()) as log:
            with patch("teardown_cost_campaign.time.monotonic", side_effect=[0, 0, 30, 30]):
                with self.assertRaises(subprocess.TimeoutExpired) as caught:
                    wait_with_progress(child, 30, output, "profile")
            self.assertEqual(caught.exception.timeout, 30)
            child.wait.assert_called_once_with(timeout=30)
            self.assertIn("validation pending", log.getvalue())

    def test_overall_budget_caps_child_without_extension(self):
        child = Mock(args=["fake"])
        child.wait.side_effect = subprocess.TimeoutExpired(child.args, 5)
        with tempfile.TemporaryDirectory() as output, contextlib.redirect_stdout(io.StringIO()):
            with patch("teardown_cost_campaign.time.monotonic", side_effect=[0, 0, 5, 5]):
                with self.assertRaises(subprocess.TimeoutExpired) as caught:
                    wait_with_progress(child, 1500, output, "profile", deadline=5)
            self.assertEqual(caught.exception.timeout, 5)
            child.wait.assert_called_once_with(timeout=5)

    def test_expired_overall_budget_does_not_wait(self):
        child = Mock(args=["fake"])
        with tempfile.TemporaryDirectory() as output, contextlib.redirect_stdout(io.StringIO()):
            with patch("teardown_cost_campaign.time.monotonic", return_value=10):
                with self.assertRaises(subprocess.TimeoutExpired):
                    wait_with_progress(child, 1500, output, "profile", deadline=5)
            child.wait.assert_not_called()

    def test_unsuccessful_exit_is_preserved(self):
        child = Mock(args=["fake"])
        child.wait.return_value = 137
        with tempfile.TemporaryDirectory() as output, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(wait_with_progress(child, 1500, output, "profile"), 137)

    def test_sample_presence_is_not_reported_as_success(self):
        child = Mock(args=["fake"])
        child.wait.side_effect = [subprocess.TimeoutExpired(child.args, 30), 0]
        with tempfile.TemporaryDirectory() as output, contextlib.redirect_stdout(io.StringIO()) as log:
            sample = Path(output) / "samples" / "scenario" / "candidate-r1.json"
            sample.parent.mkdir(parents=True)
            sample.write_text("incomplete JSON")
            with patch("teardown_cost_campaign.time.monotonic", side_effect=[0, 0, 30, 30]):
                self.assertEqual(wait_with_progress(child, 1500, output, "profile"), 0)
            self.assertIn("sample files present 1", log.getvalue())
            self.assertIn("validation pending", log.getvalue())


class AcceptanceScopeTests(unittest.TestCase):
    def test_embedio_fault_remains_required(self):
        failures, controls = [], []
        record_fault_failure("embedio", "fault-H3", ValueError("unfinished tasks"), failures, controls)
        self.assertEqual(failures, ["fault-H3: unfinished tasks"])
        self.assertEqual(controls, [])

    def test_kestrel_failure_remains_explicit(self):
        failures, controls = [], []
        record_fault_failure("kestrel", "fault-H3", UnsettledLifetimeObserver("unfinished tasks"), failures, controls)
        self.assertEqual(failures, [])
        self.assertEqual(controls, [{"sample": "fault-H3", "status": "failed-control", "error": "unfinished tasks"}])

    def test_unknown_kestrel_failure_remains_required(self):
        failures, controls = [], []
        record_fault_failure("kestrel", "fault-H3", ValueError("invalid evidence"), failures, controls)
        self.assertEqual(failures, ["fault-H3: invalid evidence"])
        self.assertEqual(controls, [])

    def test_kestrel_timeout_remains_required(self):
        failures, controls = [], []
        record_fault_failure("kestrel", "fault-H3", subprocess.TimeoutExpired("child", 180), failures, controls)
        self.assertTrue(failures)
        self.assertEqual(controls, [])

    def test_unknown_engine_is_not_exempt(self):
        with self.assertRaises(ValueError):
            record_fault_failure("unknown", "fault", ValueError("failure"), [], [])


if __name__ == "__main__": unittest.main()
