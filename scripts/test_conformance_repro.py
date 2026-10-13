"""Bounded shell orchestration controls with fake peers; never starts dotnet."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
BASH = next((str(p) for p in (Path('C:/Program Files/Git/bin/bash.exe'),) if p.exists()), shutil.which('bash'))


def shell_path(path):
    value = Path(path).resolve().as_posix()
    return '/' + value[0].lower() + value[2:] if os.name == 'nt' else value


@unittest.skipUnless(BASH, 'Bash is required for the shell wrapper controls')
class ConformanceReproTest(unittest.TestCase):
    def invoke(self, driver='h1', **settings):
        with tempfile.TemporaryDirectory(prefix='repro-control-') as temporary:
            folder = Path(temporary)
            fake = folder / 'bin'
            fake.mkdir()
            dotnet = fake / 'dotnet'
            dotnet.write_text('''#!/usr/bin/env bash
if [[ "$1" == build ]]; then
  echo build >> "$CALLS"
  exit "${BUILD_CODE:-0}"
fi
if [[ "$2" == serve ]]; then
  if [[ "${READY:-yes}" != yes ]]; then echo boot-failed; exec 1>&-; IFS= read -r -t 2 command; exit 9; fi
  echo 'IDENTITY mock-peer'
  IFS= read -r -t 2 command || exit 92
  [[ "$command" == stop ]] || exit 93
  echo stopped
  exit "${SERVER_CODE:-0}"
fi
echo driver >> "$CALLS"
echo driver-output
exit "${DRIVER_CODE:-0}"
''', encoding='utf-8', newline='\n')
            dotnet.chmod(0o755)
            calls = folder / 'calls.txt'
            environment = os.environ.copy()
            environment.update(settings)
            environment['CALLS'] = shell_path(calls)
            out = folder / 'out'
            command = [BASH, '-c', 'export PATH="$1:$PATH"; shift; exec bash "$@"', '--',
                       shell_path(fake), shell_path(ROOT / 'test/EmbedIO.Conformance/repro.sh'),
                       shell_path(folder), shell_path(out), driver]
            completed = subprocess.run(command, env=environment, capture_output=True, text=True, timeout=6)
            return completed, calls.read_text() if calls.exists() else '', {
                path.name: path.read_text() for path in out.glob('*.log')}

    def test_unknown_driver_is_rejected_before_build(self):
        result, calls, _ = self.invoke('unknown')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, '')

    def test_missing_readiness_never_runs_driver(self):
        result, calls, logs = self.invoke(READY='no')
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('driver', calls)
        self.assertIn('boot-failed', logs['server.log'])

    def test_server_failure_cannot_be_a_success(self):
        result, calls, _ = self.invoke(SERVER_CODE='7')
        self.assertEqual(result.returncode, 7, result.stderr)
        self.assertIn('driver', calls)

    def test_driver_failure_is_preserved(self):
        result, _, logs = self.invoke(DRIVER_CODE='6')
        self.assertEqual(result.returncode, 6, result.stderr)
        self.assertIn('driver-output', logs['driver.log'])

    def test_success_requires_both_peers(self):
        result, calls, logs = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls.splitlines(), ['build', 'driver'])
        self.assertIn('stopped', logs['server.log'])

    def test_build_failure_stops_before_peer_start(self):
        result, calls, _ = self.invoke(BUILD_CODE='4')
        self.assertEqual(result.returncode, 2)
        self.assertEqual(calls.splitlines(), ['build'])


if __name__ == '__main__':
    unittest.main()