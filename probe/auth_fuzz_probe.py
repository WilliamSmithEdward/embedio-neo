"""Run auth-fuzz on macOS through aioquic's dual-stack client socket and through quic_client's.

Throwaway diagnosis, never merged. Builds the development host against a finished run's
package feed, as validate.py does, then runs fuzz_client_certificate.py while this
process holds IPv4 ports, and again without them.
"""
import argparse
import json
import os
import re
import resource
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ('<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear />'
          '<add key="preview" value="{feed}" /><add key="nuget" value="https://api.nuget.org/v3/index.json" />'
          '</packageSources><packageSourceMapping><packageSource key="preview"><package pattern="EmbedIO-Neo" />'
          '<package pattern="EmbedIO-Neo.Quic.Native" /></packageSource><packageSource key="nuget">'
          '<package pattern="*" /></packageSource></packageSourceMapping></configuration>')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--feed', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--held', type=int, default=4096)
    parser.add_argument('--induced', type=int, default=3)
    parser.add_argument('--natural-dual-stack', type=int, default=12)
    parser.add_argument('--natural-ipv4', type=int, default=4)
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True)
    soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
    resource.setrlimit(resource.RLIMIT_NOFILE, (max(soft, args.held + 1024), hard))
    version = re.search(r'<Version>([^<]+)</Version>', (ROOT / 'Directory.Build.props').read_text()).group(1)
    environment = {**os.environ, 'NUGET_PACKAGES': str(out / 'nuget')}
    (out / 'nuget.config').write_text(CONFIG.format(feed=args.feed.resolve()), encoding='utf-8')
    project = str(ROOT / 'test/EmbedIO.Quic/EmbedIO.Quic.csproj')
    properties = ['-p:EmbedIOQuicPackageVersion=' + version, '-p:NuGetLockFilePath=' + str(out / 'host.lock.json'),
                  '-r', 'osx-arm64']
    for command in (['dotnet', 'restore', project, '--configfile', str(out / 'nuget.config'), '--use-lock-file',
                     *properties],
                    ['dotnet', 'publish', project, '--no-restore', '-c', 'Release', '--self-contained', 'false',
                     '-o', str(out / 'host'), *properties]):
        subprocess.run(command, cwd=ROOT, env=environment, check=True)
    assembly = out / 'host/EmbedIO.Quic.Development.dll'
    results = []

    def fuzz(label, script, index, held=frozenset()):
        directory = out / f'{label}-{index}'
        started = time.monotonic()
        process = subprocess.run([sys.executable, str(ROOT / 'test/EmbedIO.Quic' / script), '--dotnet', 'dotnet',
                                  '--assembly', str(assembly), '--cases', '256', '--seed', '1729',
                                  '--output', str(directory)], cwd=ROOT, capture_output=True, timeout=300)
        receipt_path = directory / 'receipt.json'
        receipt = json.loads(receipt_path.read_text(encoding='utf-8')) if receipt_path.exists() else {}
        failures = []
        for path in sorted(directory.glob('failure-*.json')):
            failure = json.loads(path.read_text(encoding='utf-8'))
            port = failure.get('localPort')
            failure['heldByProbe'] = port in held
            failure['portIsServer'] = port is not None and port == failure.get('serverPort')
            if port is not None and not held:
                holders = subprocess.run(['sudo', '-n', 'lsof', '-nP', f'-iUDP:{port}'], capture_output=True, text=True)
                failure['lsofNow'] = holders.stdout.splitlines()
            failures.append(failure)
        entry = {'label': label, 'index': index, 'exit': process.returncode, 'passed': receipt.get('passed'),
                 'completed': receipt.get('completed'), 'failure': receipt.get('failure'),
                 'seconds': round(time.monotonic() - started, 1),
                 'diagnostics': (receipt.get('server') or {}).get('transportDiagnostics', '')[:400],
                 'failures': failures}
        results.append(entry)
        print(json.dumps(entry), flush=True)

    holders = []
    for index in range(args.held):
        holder = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        holders.append(holder)
        holder.bind(('127.0.0.1' if index % 2 == 0 else '0.0.0.0', 0))
    held = frozenset(holder.getsockname()[1] for holder in holders)
    for index in range(args.induced):
        fuzz('held-dual-stack', 'fuzz_client_certificate_dual_stack.py', index, held)
    for index in range(args.induced):
        fuzz('held-ipv4', 'fuzz_client_certificate.py', index, held)
    for holder in holders:
        holder.close()
    for index in range(args.natural_dual_stack):
        fuzz('natural-dual-stack', 'fuzz_client_certificate_dual_stack.py', index)
    for index in range(args.natural_ipv4):
        fuzz('natural-ipv4', 'fuzz_client_certificate.py', index)
    (out / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
