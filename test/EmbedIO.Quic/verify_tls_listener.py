"""Independent certificate policy and reload checks through the actual HTTP/3 listener."""
import argparse
import os
import asyncio
import importlib.metadata
import json
import ssl
from pathlib import Path
from aioquic.asyncio import connect
from aioquic.quic.configuration import QuicConfiguration
from aioquic.quic.events import ConnectionTerminated
from cryptography import x509
from cryptography.hazmat.primitives import hashes
from verify import provenance
from verify_http3 import HttpPeer


class CertificatePeer(HttpPeer):
    termination = None

    def quic_event_received(self, event):
        if isinstance(event, ConnectionTerminated):
            self.termination = event
        super().quic_event_received(event)


def configuration(certificate, server_name):
    result = QuicConfiguration(is_client=True, alpn_protocols=['h3'], server_name=server_name,
                               verify_mode=ssl.CERT_REQUIRED, idle_timeout=5)
    result.load_verify_locations(cafile=str(certificate))
    return result


def fingerprint(certificate):
    return certificate.fingerprint(hashes.SHA256()).hex()


async def exercise(args, process, port):
    default = args.output / 'certificate.pem'
    selected = args.output / 'certificate.pem.selected.pem'
    replacement = args.output / 'certificate.pem.replacement.pem'
    observations = []
    rejected = []

    def open_peer(certificate, name):
        return connect('127.0.0.1', port, configuration=configuration(certificate, name),
                       create_protocol=CertificatePeer)

    async def expect(peer, path, status, body=None, **kwargs):
        peer.authority = f'127.0.0.1:{port}'.encode()
        _, fields, actual = await peer.request(path, **kwargs)
        assert fields[b':status'] == str(status).encode(), (path, fields, actual)
        if body is not None:
            assert actual == body, (path, actual[:100])

    async def health(peer, certificate, label):
        await expect(peer, '/health', 200, b'controller HTTP/3')
        # Pinned aioquic has no public peer-certificate accessor. Inspect its
        # retained parsed certificate only to record which verified leaf arrived.
        actual = fingerprint(peer._quic.tls._peer_certificate)
        expected = fingerprint(x509.load_pem_x509_certificate(certificate.read_bytes()))
        assert actual == expected, (label, actual, expected)
        observations.append({'case': label, 'sha256': actual})

    async def reject(certificate, name, label):
        peers = []
        def create(*values, **kwargs):
            peer = CertificatePeer(*values, **kwargs)
            peers.append(peer)
            return peer
        try:
            async with connect('127.0.0.1', port, configuration=configuration(certificate, name),
                               create_protocol=create):
                raise AssertionError('Untrusted certificate admitted: ' + label)
        except ConnectionError:
            assert len(peers) == 1 and peers[0].termination is not None, label
            code = peers[0].termination.error_code
            assert code == 0x12a, (label, code)
            rejected.append({'case': label, 'code': code})

    async with open_peer(default, 'localhost') as old_default, open_peer(selected, 'Selected.EXAMPLE') as old_selected:
        await health(old_default, default, 'initial fallback')
        await health(old_selected, selected, 'initial selected SNI')
        await expect(old_default, '/publish', 401, data=b'')
        await expect(old_default, '/publish', 403, data=b'', headers=[(b'authorization', b'Bearer writer')])
        await expect(old_default, '/publish', 200, b'published', data=b'',
                     headers=[(b'authorization', b'Bearer reader')])
        async with open_peer(replacement, 'localhost') as fresh_default:
            await health(fresh_default, replacement, 'published fallback')
        async with open_peer(selected, 'selected.example') as fresh_selected:
            await health(fresh_selected, selected, 'selected SNI after publication')
        await health(old_default, default, 'original connection after publication')
        await health(old_selected, selected, 'original SNI connection after publication')
    await reject(default, 'localhost', 'old fallback trust after publication')
    await reject(replacement, 'selected.example', 'fallback trust for selected SNI')
    await reject(replacement, 'unknown.example', 'unknown hostname')
    async with open_peer(replacement, 'localhost') as peer:
        peer.authority = f'127.0.0.1:{port}'.encode()
        _, pending = peer.start_request('/drain')
        process.stdin.write(b'drain\n')
        await process.stdin.drain()
        fields, body = await asyncio.wait_for(pending, 8)
        assert dict(fields)[b':status'] == b'200' and body == b'd' * 131072
    return observations, rejected


async def main(args):
    lock = Path(__file__).resolve().parents[1] / 'EmbedIO.Conformance/drivers/requirements.txt'
    pins = [line.split('==', 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith('aioquic==')]
    if len(pins) != 1 or importlib.metadata.version('aioquic') != pins[0]:
        raise RuntimeError('Independent peer does not match the repository dependency lock')
    args.output.mkdir(parents=True, exist_ok=False)
    process = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, 'serve-webserver-policy', args.openssl,
        str(args.output / 'certificate.pem'), str(args.output / 'unused-key.pem'), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    ready = {}
    startup = bytearray()
    failure = None
    observations, rejected = [], []
    try:
        async with asyncio.timeout(8):
            while not ready.get('ready'):
                line = await process.stdout.readline()
                startup.extend(line)
                if not line:
                    raise RuntimeError('Host ended before readiness')
                if line.startswith(b'{'):
                    ready = json.loads(line)
        async with asyncio.timeout(20):
            observations, rejected = await exercise(args, process, ready['port'])
        stdout, stderr = await asyncio.wait_for(process.communicate(), 10)
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        if process.returncode is None:
            process.kill()
        stdout, stderr = await process.communicate()
    (args.output / 'host.stdout.log').write_bytes(startup + stdout)
    (args.output / 'host.stderr.log').write_bytes(stderr)
    reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b'{')]
    report = reports[-1] if reports else {}
    result = {**provenance(args), 'passed': failure is None and process.returncode == 0
              and report.get('completed') is True and report.get('aborted') == 0
              and report.get('drainCompleted') is True and report.get('rebind') is True
              and report.get('healthRequests') == 6 and report.get('numberRequests') == 0
              and report.get('secureRequests') == 0 and report.get('publications') == 1
              and len(observations) == 6 and len(rejected) == 3,
              'failure': failure, 'server': report, 'certificates': observations, 'rejected': rejected,
              'scope': 'Actual HTTP/3 listener with library Schannel TLS, non-exportable RSA/ECDSA keys, SNI policy, live certificate publication, authenticated controller, existing connections, strict peer verification, drain and rebind.'}
    (args.output / 'receipt.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('passed', 'failure', 'server', 'certificates', 'rejected')}), flush=True)
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dotnet', 'assembly'):
        parser.add_argument('--' + option, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.openssl = 'schannel-sni' if os.name == 'nt' else 'portable-sni'
    raise SystemExit(asyncio.run(main(args)))
