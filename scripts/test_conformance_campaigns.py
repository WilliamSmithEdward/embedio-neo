"""Campaign wrapper controls using fake executables; no protocol/provider work."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from test_conformance_repro import BASH, ROOT, shell_path


@unittest.skipUnless(BASH, 'Bash is required')
class CampaignTest(unittest.TestCase):
    def invoke(self, script='run-lifecycle-campaigns.sh', **settings):
        with tempfile.TemporaryDirectory(prefix='campaign-control-') as temporary:
            folder = Path(temporary)
            bin_path = folder / 'bin'
            bin_path.mkdir()
            fake = bin_path / 'dotnet'
            fake.write_text('''#!/usr/bin/env bash
if [[ "$1" == --version ]]; then echo mock-version; exit 0; fi
if [[ "$1" == build ]]; then echo build >> "$CALLS"; exit 0; fi
if [[ "$2" != serve ]]; then echo driver >> "$CALLS"; exit 0; fi
echo READY mock-peer
if [[ "${READY:-yes}" == yes ]]; then echo IDENTITY mock-peer; else exec 1>&-; fi
while IFS= read -r -t 2 command; do
  case $command in
    stats) if [[ "${STATS:-yes}" == yes ]]; then echo STATS '{}'; else exec 1>&-; fi ;;
    stop) echo ENDPOINT-STOPPED; exit "${SERVER_CODE:-0}";;
  esac
done
exit 91
''', newline='\n')
            fake.chmod(0o755)
            for name in ('python3', 'h2spec', 'sha256sum', 'dpkg-query'):
                tool = bin_path / name
                tool.write_text('#!/usr/bin/env bash\necho mock-tool\nexit 0\n', newline='\n')
                tool.chmod(0o755)
            calls = folder / 'calls'
            environment = os.environ.copy()
            environment.update(ONLY='sections', CALLS=shell_path(calls))
            environment.update(settings)
            result = subprocess.run([BASH, '-c', 'export PATH="$1:$PATH"; shift; exec bash "$@"', '--',
                shell_path(bin_path), shell_path(ROOT / 'test/EmbedIO.Conformance' / script),
                shell_path(folder), shell_path(folder / 'out'), '1729', '1'],
                env=environment, capture_output=True, text=True, timeout=8)
            return result, calls.read_text() if calls.exists() else ''

    def test_unknown_selection_fails_before_build(self):
        result, calls = self.invoke(ONLY='typo')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, '')

    def test_missing_identity_fails(self):
        result, _ = self.invoke(READY='no')
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('overall=0', result.stdout)

    def test_missing_stats_fails(self):
        result, _ = self.invoke(STATS='no')
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('overall=0', result.stdout)

    def test_successful_owned_peer_exit_is_joined(self):
        for script in ('run-lifecycle-campaigns.sh', 'run-campaigns.sh'):
            with self.subTest(script=script):
                result, _ = self.invoke(script)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn('overall=0', result.stdout)

    def test_failed_server_cannot_pass(self):
        result, _ = self.invoke(SERVER_CODE='7')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('exit=7', result.stdout)


if __name__ == '__main__':
    unittest.main()
