"""Verify benchmark process ownership and shared-lock boundaries without load."""
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

import guarded_load_benchmark as guard

TEMP_ROOT = Path(__file__).resolve().parents[1] / "TestResults" / "guard-tests"
TEMP_ROOT.mkdir(parents=True, exist_ok=True)

class GuardTests(unittest.TestCase):
    def test_sibling_jobs_are_not_related(self):
        table = [(1, 0, "root"), (100, 1, "shell"), (200, 100, "guard"),
                 (300, 200, "client"), (301, 300, "server"),
                 (400, 100, "foreign tests"), (500, 400, "foreign child")]
        with patch.object(guard.os, "getpid", return_value=200):
            related = guard.related(table, {300})
        self.assertEqual(related, {0, 1, 100, 200, 300, 301})

    def test_foreign_sibling_load_is_reported(self):
        table = [(1, 0, "root"), (200, 1, "guard"),
                 (300, 200, "LoadBenchmark own"), (400, 1, "dotnet test foreign")]
        with patch.object(guard.os, "getpid", return_value=200), patch.object(guard, "process_table", return_value=table):
            found = guard.foreign_processes([], {300})
        self.assertEqual([pid for pid, _ in found], [400])

    def test_lock_does_not_overwrite_or_remove_another_owner(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as directory:
            lock = Path(directory) / "lock"
            lock.write_text("another owner\n", encoding="utf-8")
            self.assertFalse(guard.try_acquire(lock, "ours", 1, []))
            guard.release(lock, "ours")
            self.assertEqual(lock.read_text(encoding="utf-8"), "another owner\n")

    def test_owned_lock_is_removed(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as directory:
            lock = Path(directory) / "lock"
            self.assertTrue(guard.try_acquire(lock, "ours", 1, []))
            self.assertTrue(guard.lock_is_ours(lock, "ours"))
            guard.release(lock, "ours")
            self.assertFalse(lock.exists())

    def test_completed_child_is_never_killed(self):
        child = Mock()
        child.poll.return_value = 0
        with patch.object(guard.subprocess, "run") as run, patch.object(guard.os, "killpg", create=True) as kill:
            guard.kill_tree(child)
        run.assert_not_called()
        kill.assert_not_called()

    def test_monitor_failure_cleans_up_child_before_unlock(self):
        with tempfile.TemporaryDirectory(dir=TEMP_ROOT) as directory:
            lock = Path(directory) / "lock"
            child = Mock()
            child.poll.return_value = None
            def check_locked(*_args, **_kwargs):
                self.assertTrue(lock.exists())
            child.wait.side_effect = check_locked
            args = ["guard", "--owner", "test", "--output", str(Path(directory) / "out"), "--lock", str(lock), "--", "fake"]
            with patch.object(guard.sys, "argv", args), patch.object(guard, "wait_idle"), \
                    patch.object(guard.subprocess, "Popen", return_value=child), patch.object(guard.time, "sleep"), \
                    patch.object(guard, "foreign_processes", side_effect=RuntimeError("monitor failed")), \
                    patch.object(guard, "kill_tree", side_effect=check_locked) as kill:
                with self.assertRaisesRegex(RuntimeError, "monitor failed"):
                    guard.main()
            kill.assert_called_once_with(child)
            child.wait.assert_called_once_with(timeout=15)
            self.assertFalse(lock.exists())


if __name__ == "__main__":
    unittest.main()
