"""Seeded concurrent full-handshake native client-certificate mutations."""
import argparse
import os
import asyncio
import collections
import hashlib
import importlib.metadata
import shutil
import json
import math
import random
import time
from pathlib import Path
from aioquic.asyncio import connect
from aioquic.quic.logger import QuicLogger
from cryptography import x509
from cryptography.hazmat.primitives import serialization, hashes
from verify import provenance
from verify_client_certificate import certificates
from verify_tls_listener import CertificatePeer, configuration


async def main(args):
    lock = Path(__file__).resolve().parents[1] / 'EmbedIO.Conformance/drivers/requirements.txt'
    pins = [line.split('==', 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith('aioquic==')]
    if len(pins) != 1 or importlib.metadata.version('aioquic') != pins[0]:
        raise RuntimeError('Independent peer does not match the repository dependency lock')
    args.output.mkdir(parents=True, exist_ok=False)
    replay = None
    replay_payload = None
    if args.replay:
        replay = json.loads(args.replay.read_text())
        replay_payload = args.replay.with_suffix('.der').read_bytes()
        if not 0 <= replay['kind'] <= 6 or len(replay_payload) > 4096 or hashlib.sha256(replay_payload).hexdigest() != replay['sha256']:
            raise RuntimeError('Replay input is invalid or does not match its receipt')
        for name in ('client.pem', 'client.key', 'other.pem', 'other.key', 'client-root.pem'):
            shutil.copyfile(args.replay.parent / name, args.output / name)
        identities = {'client': x509.load_pem_x509_certificate((args.output / 'client.pem').read_bytes()).fingerprint(hashes.SHA256()).hex().upper().encode()}
        args.cases = 1
    else:
        identities, _ = certificates(args.output, 'http://127.0.0.1:1/unused')
    original = x509.load_pem_x509_certificate((args.output / 'client.pem').read_bytes()).public_bytes(serialization.Encoding.DER)
    alternate = x509.load_pem_x509_certificate((args.output / 'other.pem').read_bytes()).public_bytes(serialization.Encoding.DER)
    budget = max(30, min(1800, 30 + math.ceil(args.cases / 16)))
    process = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, 'serve-webserver-client-certificate-fuzz',
        str(args.output / 'certificate.pem'), str(args.output / 'unused-key.pem'), str(budget),
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    ready = {}
    startup = bytearray()
    codes = collections.Counter()
    completed = 0
    successes = 0
    health = 0
    failure = None
    started = time.monotonic()

    def open_peer(payload=None, mismatch=False):
        config = configuration(args.output / 'certificate.pem', 'localhost')
        config.quic_logger = QuicLogger() if payload is not None else None
        config.load_cert_chain(str(args.output / 'client.pem'), str(args.output / ('other.key' if mismatch else 'client.key')))
        if payload is not None:
            class CertificateBytes:
                def public_bytes(self, encoding):
                    return payload
            config.certificate = CertificateBytes()
        peers = []
        def create(*values, **kwargs):
            peer = CertificatePeer(*values, **kwargs)
            peer.mutation_trace = config.quic_logger
            peers.append(peer)
            return peer
        return connect('127.0.0.1', ready['port'], configuration=config, create_protocol=create), peers

    async def identity(peer):
        peer.authority = f"127.0.0.1:{ready['port']}".encode()
        _, headers, body = await peer.request('/client')
        assert headers[b':status'] == b'200' and body == identities['client']

    def mutation(index):
        if replay is not None:
            return replay['kind'], replay_payload
        rng = random.Random((args.seed << 32) ^ index)
        kind = index % 7
        payload = bytearray(original)
        if kind in (0, 1):
            pass
        elif kind == 2:
            payload = bytearray(rng.randbytes(rng.randrange(0, 4097)))
        elif kind == 3:
            del payload[rng.randrange(len(payload)):]
        elif kind == 4:
            offset = rng.randrange(len(payload))
            payload[offset] ^= 1 << rng.randrange(8)
        elif kind == 5:
            payload.extend(rng.randbytes(rng.randrange(1, 65)))
        else:
            payload = bytearray(alternate)
        return kind, bytes(payload)

    async def worker(worker_index, width):
        nonlocal completed, successes
        for index in range(worker_index, args.cases, width):
            kind, payload = mutation(index)
            context, peers = open_peer(payload, kind == 1)
            try:
                async with asyncio.timeout(8):
                    try:
                        async with context as peer:
                            await identity(peer)
                            assert kind not in (1, 6), 'A different key authenticated as the pinned client'
                            successes += 1
                    except ConnectionError:
                        assert kind != 0, 'An unchanged credential was rejected'
                        assert peers and peers[0].termination is not None
                        code = peers[0].termination.error_code
                        assert code in (0x128, 0x12a, 0x12b, 0x12d, 0x12e, 0x12f, 0x132, 0x133, 0x174), code
                        codes[str(code)] += 1
                completed += 1
            except asyncio.CancelledError:
                raise
            except BaseException as error:
                if peers:
                    (args.output / f'failure-{index}.qlog').write_text(json.dumps(peers[0].mutation_trace.to_dict()), encoding='utf-8')
                (args.output / f'failure-{index}.der').write_bytes(payload)
                (args.output / f'failure-{index}.json').write_text(json.dumps({'seed': args.seed, 'index': index,
                    'kind': kind, 'failure': repr(error), 'termination': repr(peers[0].termination) if peers else None, 'sha256': hashlib.sha256(payload).hexdigest()}), encoding='utf-8')
                raise

    try:
        async with asyncio.timeout(8):
            while not ready.get('ready'):
                line = await process.stdout.readline()
                startup.extend(line)
                if not line:
                    raise RuntimeError('Host ended before readiness')
                if line.startswith(b'{'):
                    ready = json.loads(line)
        context, _ = open_peer()
        async with asyncio.timeout(budget):
            async with context as healthy:
                await identity(healthy)
                width = min(args.cases, 16)
                async with asyncio.TaskGroup() as group:
                    for index in range(width):
                        group.create_task(worker(index, width))
                    async def check_sibling():
                        nonlocal health
                        healthy.authority = f"127.0.0.1:{ready['port']}".encode()
                        while completed < args.cases:
                            _, fields, body = await healthy.request('/health')
                            assert fields[b':status'] == b'200' and body == b'controller HTTP/3'
                            health += 1
                            await asyncio.sleep(0.05)
                    group.create_task(check_sibling())
                await identity(healthy)
                _, pending = healthy.start_request('/drain')
                process.stdin.write(b'drain\n')
                await process.stdin.drain()
                fields, body = await asyncio.wait_for(pending, 8)
                assert dict(fields)[b':status'] == b'200' and body == b'd' * 131072
        stdout, stderr = await asyncio.wait_for(process.communicate(), 10)
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        try:
            if process.returncode is None:
                process.stdin.write(b'stop\n')
                await process.stdin.drain()
            stdout, stderr = await asyncio.wait_for(process.communicate(), 10)
        except (ConnectionError, asyncio.TimeoutError):
            if process.returncode is None:
                process.kill()
            stdout, stderr = await process.communicate()
    (args.output / 'host.stdout.log').write_bytes(startup + stdout)
    (args.output / 'host.stderr.log').write_bytes(stderr)
    reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b'{')]
    report = reports[-1] if reports else {}
    result = {**provenance(args), 'passed': failure is None and process.returncode == 0 and completed == args.cases
        and health > 0 and report.get('completed') is True and report.get('drainCompleted') is True
        and report.get('rebind') is True and report.get('healthRequests') == health
        and report.get('clientCertificateRequests') == successes + 2
        and report.get('retainedCertificates') == min(32, successes + 2),
        'replaySource': str(args.replay) if args.replay else None,
        'seed': args.seed, 'cases': args.cases, 'completed': completed, 'acceptedPinnedIdentities': successes,
        'rejections': dict(codes), 'healthySiblingRequests': health, 'failure': failure,
        'elapsedSeconds': round(time.monotonic() - started, 3), 'server': report,
        'scope': 'Encrypted full TLS handshakes with deterministic certificate DER/key mutations, pinned identity oracle, concurrent healthy connection, controller identity, cleanup/drain/rebind; native code is not coverage-instrumented.'}
    (args.output / 'receipt.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key not in ('sourceHashes', 'server')}), flush=True)
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dotnet', 'assembly'):
        parser.add_argument('--' + option, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--replay', type=Path, help='Replay a retained failure JSON and its DER/ephemeral fixture')
    parser.add_argument('--seed', type=int, default=1729)
    parser.add_argument('--cases', type=int, default=256)
    args = parser.parse_args()
    if not 1 <= args.cases <= 100000 or not 1 <= args.seed <= 2147483647:
        parser.error('Cases or seed are outside supported bounds')
    args.openssl = 'schannel-rsa' if os.name == 'nt' else 'portable-rsa'
    raise SystemExit(asyncio.run(main(args)))
