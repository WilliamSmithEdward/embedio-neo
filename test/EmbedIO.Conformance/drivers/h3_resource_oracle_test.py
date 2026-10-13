"""Offline H3 resource verdict controls; no aioquic import or live protocol work.

Only the exact driver orchestration functions are compiled from its AST. Protocol
case functions and fuzz iterations are not executed; their preservation is checked
separately by the source-diff receipt. --driver permits a retained baseline input.
"""
import argparse
import ast
import asyncio
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import random
import sys
import time
import types
import unittest
from unittest.mock import AsyncMock, patch


HERE = Path(__file__).resolve().parent
TARGET = HERE / "h3_campaign.py"
if "--driver" in sys.argv:
    position = sys.argv.index("--driver")
    TARGET = Path(sys.argv[position + 1]).resolve()
    del sys.argv[position:position + 2]

spec = importlib.util.spec_from_file_location("h3_resource_control_helper", HERE / "resource_observation.py")
if spec is None or spec.loader is None:
    raise ImportError("Missing anchored resource helper")
resource = importlib.util.module_from_spec(spec)
spec.loader.exec_module(resource)


def snapshot(active=0, handles=10, managed=100):
    return {"activeHandlers": active, "handles": handles, "managedBytes": managed}


def driver():
    tree = ast.parse(TARGET.read_text(encoding="utf-8-sig"), filename=str(TARGET))
    names = {"run_cases", "run_fuzz", "settle", "main"}
    nodes = [node for node in tree.body if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in names]
    if len(nodes) != len(names) or {node.name for node in nodes} != names:
        raise AssertionError("Expected exactly the four original driver orchestration functions")
    scope = dict(argparse=argparse, asyncio=asyncio, json=json, random=random,
                 sys=sys, time=time, _resource=resource, ONLY=None, CASES=[],
                 file_bytes=lambda: b"", stats=resource.stats,
                 Session=lambda host, port: types.SimpleNamespace(host=host, port=port))
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(TARGET), "exec"), scope)
    return scope


class H3ResourceOracleTest(unittest.TestCase):
    def report(self, mode, before, after, requested=True):
        scope = driver()
        case = AsyncMock(return_value=("conforms", "offline control"))
        scope.update(CASES=[("control", "offline", "MUST", case)],
                     healthy=AsyncMock(return_value=None),
                     stats=lambda *_: before, settle=AsyncMock(return_value=after))
        session = types.SimpleNamespace(host="offline.invalid")
        with contextlib.redirect_stdout(io.StringIO()):
            if mode == "cases":
                report = asyncio.run(scope["run_cases"](session, 1 if requested else None))
                case.assert_awaited_once()
                self.assertEqual("conforms", report["outcomes"][0]["result"])
            else:
                report = asyncio.run(scope["run_fuzz"](session, 17, 0, 1 if requested else None))
                self.assertEqual(0, report["totals"]["connections"])
        return report

    def test_unavailable_or_invalid_observations_fail_both_modes(self):
        pairs = [(None, None), (None, snapshot()), (snapshot(), None),
                 ({"error": "offline", **snapshot()}, snapshot()),
                 (snapshot(), {"error": "offline", **snapshot()}),
                 ([], snapshot()), (snapshot(), {}),
                 (snapshot(), snapshot(handles=True)),
                 (snapshot(), snapshot(managed=-1)),
                 (snapshot(), snapshot(active=1))]
        for mode in ("cases", "fuzz"):
            for before, after in pairs:
                with self.subTest(mode=mode, before=before, after=after):
                    report = self.report(mode, before, after)
                    self.assertIn("error", report)
                    if mode == "fuzz":
                        self.assertEqual("FAIL", report["result"])

    def test_valid_settled_observations_pass_both_modes(self):
        for mode in ("cases", "fuzz"):
            with self.subTest(mode=mode):
                report = self.report(mode, snapshot(), snapshot())
                self.assertNotIn("error", report)
                if mode == "fuzz":
                    self.assertEqual("PASS", report["result"])

    def test_unrequested_stats_remain_optional(self):
        for mode in ("cases", "fuzz"):
            with self.subTest(mode=mode):
                report = self.report(mode, None, None, requested=False)
                self.assertNotIn("error", report)
                self.assertIsNone(report["stats_before"])
                self.assertIsNone(report["stats_after"])

    def test_fuzz_growth_limits_preserve_boundary_and_diagnostics(self):
        for handles, managed, failed in [(74, 100 + (32 << 20), False),
                                        (75, 100, True), (10, 101 + (32 << 20), True)]:
            with self.subTest(handles=handles, managed=managed):
                report = self.report("fuzz", snapshot(), snapshot(handles=handles, managed=managed))
                self.assertEqual("FAIL" if failed else "PASS", report["result"])
                self.assertEqual(handles - 10, report["handleGrowth"])
                self.assertEqual(managed - 100, report["managedGrowth"])

    def test_main_exit_fails_for_resource_error_in_both_modes(self):
        for mode in ("cases", "fuzz"):
            for before, after, expected in [(None, None, 1),
                                            (snapshot(), snapshot(active=1), 1),
                                            (snapshot(), snapshot(), 0)]:
                with self.subTest(mode=mode, expected=expected):
                    scope = driver()
                    scope.update(stats=lambda *_: before, settle=AsyncMock(return_value=after),
                                 healthy=AsyncMock(return_value=None), CASES=[])
                    argv = ["h3_campaign.py", mode, "--port", "1", "--stats-port", "2", "--iterations", "0"]
                    fake = types.ModuleType("aioquic")
                    fake.__version__ = "offline-control-only"
                    with patch.object(sys, "argv", argv), patch.dict(sys.modules, {"aioquic": fake}), contextlib.redirect_stdout(io.StringIO()):
                        with self.assertRaises(SystemExit) as raised:
                            scope["main"]()
                    self.assertEqual(expected, raised.exception.code)

    def test_settle_stops_on_invalid_observation_without_sleep(self):
        for value in (None, {}, {"error": "offline", **snapshot()}):
            with self.subTest(value=value):
                scope = driver()
                scope["stats"] = lambda *_: value
                with patch.object(asyncio, "sleep", new_callable=AsyncMock) as sleep:
                    self.assertEqual(value, asyncio.run(scope["settle"](types.SimpleNamespace(host="offline.invalid"), 1)))
                    sleep.assert_not_awaited()

    def test_settle_retains_bounded_polling_for_active_handlers(self):
        scope = driver()
        scope["stats"] = lambda *_: snapshot(active=1)
        with patch.object(asyncio, "sleep", new_callable=AsyncMock) as sleep:
            self.assertEqual(1, asyncio.run(scope["settle"](types.SimpleNamespace(host="offline.invalid"), 1))["activeHandlers"])
            self.assertEqual(50, sleep.await_count)
            self.assertTrue(all(call.args == (0.1,) for call in sleep.await_args_list))


if __name__ == "__main__":
    unittest.main()
