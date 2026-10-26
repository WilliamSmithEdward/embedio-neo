"""Independent TLS session-ticket checks through the actual HTTP/3 listener.

The host's ticket keys rotate every two seconds and its tickets live six, so one run covers
resumption, a ticket from a rotated key, expiry, key retirement and tickets presented under another
certificate. No ticket may invite early data.
"""
import argparse
import asyncio
import dataclasses
import datetime
import importlib.metadata
import json
import os
import time
from pathlib import Path
from aioquic.asyncio import connect
from cryptography import x509
from verify import provenance
from verify_tls_listener import CertificatePeer, configuration, fingerprint

ROTATION = 2.0
LIFETIME = 6.0


async def exercise(args, port, cases):
    default = args.output / 'certificate.pem'
    selected = args.output / 'certificate.pem.selected.pem'

    async def handshake(label, certificate, name, ticket=None, publish=False):
        tickets = []
        options = configuration(certificate, name)
        options.session_ticket = ticket
        async with connect('127.0.0.1', port, configuration=options, create_protocol=CertificatePeer,
                           session_ticket_handler=tickets.append) as peer:
            peer.authority = f'127.0.0.1:{port}'.encode()
            _, fields, body = await peer.request('/health')
            assert fields[b':status'] == b'200' and body == b'controller HTTP/3', (label, fields, body)
            if publish:
                _, fields, body = await peer.request('/publish', data=b'', headers=[(b'authorization', b'Bearer reader')])
                assert fields[b':status'] == b'200' and body == b'published', (label, fields, body)
            # The server sends its ticket with the end of the handshake, before any response.
            assert tickets, (label, 'no session ticket')
            # A ticket that allowed early data would invite 0-RTT, which the server does not accept.
            assert tickets[-1].max_early_data_size is None, (label, 'ticket invites early data')
            resumed = peer._quic.tls.session_resumed
            leaf = peer._quic.tls._peer_certificate
            if not resumed:
                # A full handshake proves the certificate the server name selects now.
                expected = fingerprint(x509.load_pem_x509_certificate(certificate.read_bytes()))
                assert leaf is not None and fingerprint(leaf) == expected, (label, 'unexpected certificate')
        lifetime = (tickets[-1].not_valid_after - tickets[-1].not_valid_before).total_seconds()
        cases.append({'case': label, 'resumed': resumed, 'tickets': len(tickets), 'offered': ticket is not None,
                      'lifetime': lifetime})
        return resumed, tickets[-1]

    def offered_as(ticket, name):
        # The client offers a ticket only for the server name it was issued for, and only
        # while it has not expired; these cases need the server to decide instead.
        return dataclasses.replace(ticket, server_name=name,
                                   not_valid_after=datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1))

    resumed, first = await handshake('full handshake', default, 'localhost')
    assert not resumed
    # Keys rotate lazily, when a handshake finds the newest older than the rotation period.
    started = time.monotonic()
    resumed, renewed = await handshake('resumed', default, 'localhost', first)
    assert resumed, 'the server did not resume its own ticket'
    issued = time.monotonic()
    resumed, _ = await handshake('ticket under another certificate', selected, 'Selected.EXAMPLE',
                                 offered_as(first, 'Selected.EXAMPLE'))
    assert not resumed, 'a ticket resumed under another certificate'
    resumed, other = await handshake('selected certificate full handshake', selected, 'Selected.EXAMPLE')
    assert not resumed
    resumed, _ = await handshake('selected certificate resumed', selected, 'Selected.EXAMPLE', other)
    assert resumed, 'the selected certificate did not resume its own ticket'
    resumed, _ = await handshake('ticket presented for another server name', default, 'localhost',
                                 offered_as(other, 'localhost'))
    assert not resumed, 'a ticket resumed under another certificate'

    await asyncio.sleep(max(0.0, started + ROTATION + 0.3 - time.monotonic()))
    resumed, _ = await handshake('rotating handshake', default, 'localhost')
    assert not resumed
    # renewed was sealed with the key the rotation retired from issuing; it must still open.
    assert time.monotonic() - issued < LIFETIME - 2.0, 'the rotation case ran too late to decide'
    resumed, _ = await handshake('ticket from the previous key', default, 'localhost', renewed)
    assert resumed, 'a ticket from the previous key did not resume'

    # OpenSSL refuses a ticket older than its lifetime while its key still opens it. Schannel
    # announces a lifetime of its own and does not check the server's, so on Windows the
    # ticket resumes until its key retires.
    await asyncio.sleep(max(0.0, issued + LIFETIME + 0.6 - time.monotonic()))
    resumed, _ = await handshake('ticket past its lifetime', default, 'localhost', offered_as(renewed, 'localhost'))
    assert resumed == (os.name == 'nt'), 'a ticket past its lifetime was handled unexpectedly'
    # The key that sealed it retires once older than the rotation period plus the lifetime.
    await asyncio.sleep(max(0.0, started + ROTATION + LIFETIME + 0.6 - time.monotonic()))
    resumed, _ = await handshake('ticket from a retired key', default, 'localhost', offered_as(renewed, 'localhost'))
    assert not resumed, 'a ticket from a retired key resumed'

    # A publication replaces every credential, and with them the ticket keys.
    resumed, recent = await handshake('before publication', selected, 'Selected.EXAMPLE', publish=True)
    resumed, _ = await handshake('after publication', selected, 'Selected.EXAMPLE', recent)
    assert not resumed, 'a ticket resumed after a publication'


async def main(args):
    lock = Path(__file__).resolve().parents[1] / 'EmbedIO.Conformance/drivers/requirements.txt'
    pins = [line.split('==', 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith('aioquic==')]
    if len(pins) != 1 or importlib.metadata.version('aioquic') != pins[0]:
        raise RuntimeError('Independent peer does not match the repository dependency lock')
    args.output.mkdir(parents=True, exist_ok=False)
    process = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, 'serve-webserver-tickets', args.openssl,
        str(args.output / 'certificate.pem'), str(args.output / 'unused-key.pem'), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    ready = {}
    startup = bytearray()
    failure = None
    cases = []
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
            await exercise(args, ready['port'], cases)
        process.stdin.write(b'stop\n')
        await process.stdin.drain()
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
              and report.get('completed') is True and report.get('aborted') == 0 and report.get('rebind') is True
              and report.get('publications') == 1 and len(cases) == 12,
              'failure': failure, 'server': report, 'cases': cases,
              'scope': 'Actual HTTP/3 listener with library TLS and SNI certificate selection: ticket issue and '
                       'resumption, refusal under another certificate, key rotation, lifetime, key retirement '
                       'and publication; no ticket invites early data.'}
    (args.output / 'receipt.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('passed', 'failure', 'cases')}), flush=True)
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dotnet', 'assembly'):
        parser.add_argument('--' + option, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.openssl = 'schannel-sni' if os.name == 'nt' else 'portable-sni'
    raise SystemExit(asyncio.run(main(args)))
