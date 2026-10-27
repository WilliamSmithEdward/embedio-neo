"""Independent mutual-TLS checks through the actual HTTP/3 WebServer."""
import argparse
import os
import asyncio
import datetime
import contextlib
import uuid
import importlib.metadata
import json
from pathlib import Path
from quic_client import connect
from cryptography import x509
from cryptography.exceptions import UnsupportedAlgorithm
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID, AuthorityInformationAccessOID
from revocation_peer import make_ocsp_responder, serve_revocation
from verify import provenance
from verify_tls_listener import CertificatePeer, configuration, fingerprint


def certificates(directory, crl_base):
    now = datetime.datetime.now(datetime.timezone.utc)
    def issue(name, key, issuer=None, signer=None, ca=False, expired=False, server_only=False):
        subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, name)])
        start, end = (now - datetime.timedelta(days=2), now - datetime.timedelta(days=1)) if expired else (now - datetime.timedelta(minutes=5), now + datetime.timedelta(hours=1))
        builder = (x509.CertificateBuilder().subject_name(subject)
                   .issuer_name(issuer.subject if issuer else subject).public_key(key.public_key())
                   .serial_number(x509.random_serial_number()).not_valid_before(start).not_valid_after(end)
                   .add_extension(x509.BasicConstraints(ca=ca, path_length=1 if ca else None), critical=True)
                   .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=False, key_encipherment=False,
                                  data_encipherment=False, key_agreement=False, key_cert_sign=ca, crl_sign=ca,
                                  encipher_only=None, decipher_only=None), critical=True)
                   .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False))
        if not ca:
            builder = builder.add_extension(x509.CRLDistributionPoints([x509.DistributionPoint(
                full_name=[x509.UniformResourceIdentifier(crl_base + '/intermediate.crl')], relative_name=None,
                reasons=None, crl_issuer=None)]), critical=False)
            builder = builder.add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH if server_only else ExtendedKeyUsageOID.CLIENT_AUTH]), critical=True)
        elif issuer is not None:
            builder = builder.add_extension(x509.CRLDistributionPoints([x509.DistributionPoint(
                full_name=[x509.UniformResourceIdentifier(crl_base + '/root.crl')], relative_name=None,
                reasons=None, crl_issuer=None)]), critical=False)
        if issuer is not None:
            builder = builder.add_extension(x509.AuthorityInformationAccess([x509.AccessDescription(
                AuthorityInformationAccessOID.OCSP,
                x509.UniformResourceIdentifier(crl_base + '/ocsp/' + ('intermediate' if ca else name)))]), critical=False)
        return builder.sign(signer or key, hashes.SHA256())
    root_key = ec.generate_private_key(ec.SECP256R1())
    root = issue('temporary client root', root_key, ca=True)
    intermediate_key = ec.generate_private_key(ec.SECP256R1())
    intermediate = issue('temporary intermediate', intermediate_key, root, root_key, ca=True)
    pem = serialization.Encoding.PEM
    (directory / 'client-root.pem').write_bytes(root.public_bytes(pem))
    result = {}
    revoked_serial = None
    responders = {'/ocsp/intermediate': make_ocsp_responder(intermediate, root, root_key)}
    for name in ('client', 'other', 'expired', 'server-only', 'revoked'):
        key = ec.generate_private_key(ec.SECP256R1())
        certificate = issue(name, key, intermediate, intermediate_key, expired=name == 'expired', server_only=name == 'server-only')
        (directory / (name + '.pem')).write_bytes(certificate.public_bytes(pem) + intermediate.public_bytes(pem) + root.public_bytes(pem))
        (directory / (name + '.key')).write_bytes(key.private_bytes(pem, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        result[name] = fingerprint(certificate).upper().encode()
        responders['/ocsp/' + name] = make_ocsp_responder(certificate, intermediate, intermediate_key, name == 'revoked')
        if name == 'revoked':
            revoked_serial = certificate.serial_number
    def crl(issuer, signer, revoked=None):
        builder = (x509.CertificateRevocationListBuilder().issuer_name(issuer.subject)
                   .last_update(now - datetime.timedelta(minutes=1)).next_update(now + datetime.timedelta(hours=1))
                   .add_extension(x509.CRLNumber(1), critical=False)
                   .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(signer.public_key()), critical=False))
        if revoked is not None:
            builder = builder.add_revoked_certificate(x509.RevokedCertificateBuilder().serial_number(revoked)
                .revocation_date(now - datetime.timedelta(seconds=30)).build())
        return builder.sign(signer, hashes.SHA256()).public_bytes(serialization.Encoding.DER)
    return result, {'/root.crl': crl(root, root_key), '/intermediate.crl': crl(intermediate, intermediate_key, revoked_serial), **responders}


async def case(args, mode, client, allowed):
    label = mode + '-' + client
    output = args.output / label
    output.mkdir()
    resources = {}
    requests = []
    nonce = '/' + uuid.uuid4().hex
    async def serve_crl(reader, writer):
        if mode == 'require-custom-online' and client == 'revoked':
            # Exercise validation that outlives the loopback closing interval.
            await asyncio.sleep(0.6)
        await serve_revocation(reader, writer, resources, requests)
    crl_server = await asyncio.start_server(serve_crl, '127.0.0.1', 0, limit=8192)
    crl_port = crl_server.sockets[0].getsockname()[1]
    identities, crls = certificates(output, f'http://127.0.0.1:{crl_port}' + nonce)
    resources.update({nonce + path: body for path, body in crls.items()})
    process = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, 'serve-webserver-client-certificate', mode,
        str(output / 'certificate.pem'), str(output / 'unused-key.pem'), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    startup = bytearray()
    ready = {}
    failure = None
    peers = []
    # The listener's handshakes share one credential per certificate. The server issues a
    # session ticket only when it asks for no client certificate, so no connection resumes
    # with another's client identity.
    tickets = []
    rejection = None
    async def request(peer, path, expected, body=None, **kwargs):
        peer.authority = f"127.0.0.1:{ready['port']}".encode()
        _, headers, actual = await peer.request(path, **kwargs)
        assert headers[b':status'] == str(expected).encode(), (path, headers, actual)
        if body is not None:
            assert body == actual, (path, body, actual)
    def open_peer(kind=None):
        selected_client = kind or client
        config = configuration(output / 'certificate.pem', 'localhost')
        if selected_client != 'missing':
            source = 'client' if selected_client in ('mismatch', 'missing-intermediate', 'malformed', 'unsupported-key') else selected_client
            config.load_cert_chain(str(output / (source + '.pem')), str(output / (('other' if selected_client == 'mismatch' else source) + '.key')))
            if selected_client == 'missing-intermediate':
                config.certificate_chain = []
            if selected_client == 'malformed':
                # The pinned peer serializes this public-bytes object into the
                # encrypted TLS Certificate message; native parsing must reject it.
                class MalformedCertificate:
                    def public_bytes(self, encoding):
                        return b'\x30\x03\x01\x01\xff'
                config.certificate = MalformedCertificate()
            if selected_client == 'unsupported-key':
                original = config.certificate.public_bytes(serialization.Encoding.DER)
                curve = bytes.fromhex('06082a8648ce3d030107')
                assert original.count(curve) == 1
                unsupported = original.replace(curve, bytes.fromhex('06082a8648ce3c030107'))
                decoded = x509.load_der_x509_certificate(unsupported)
                try:
                    decoded.public_key()
                except UnsupportedAlgorithm:
                    pass
                else:
                    raise AssertionError('Fixture must contain an unsupported public key')
                class UnsupportedKeyCertificate:
                    def public_bytes(self, encoding):
                        return unsupported
                config.certificate = UnsupportedKeyCertificate()
        def create(*values, **kwargs):
            peer = CertificatePeer(*values, **kwargs)
            peers.append(peer)
            return peer
        return connect('127.0.0.1', ready['port'], configuration=config, create_protocol=create,
                       session_ticket_handler=tickets.append)
    sibling = not allowed and mode == 'require-pin' and client in ('missing', 'other', 'mismatch', 'malformed', 'unsupported-key')
    async with contextlib.AsyncExitStack() as connections:
        try:
            async with asyncio.timeout(8):
                while not ready.get('ready'):
                    line = await process.stdout.readline()
                    startup.extend(line)
                    if not line:
                        raise RuntimeError('Host ended before readiness')
                    if line.startswith(b'{'):
                        ready = json.loads(line)
            async with asyncio.timeout(12):
                healthy = await connections.enter_async_context(open_peer('client')) if sibling else None
                try:
                    async with open_peer() as peer:
                        identity = b'none' if client == 'missing' or mode == 'none' else identities.get(client, identities['client'])
                        await request(peer, '/client', 200, identity)
                        await request(peer, '/client', 200, identity)
                        if not allowed:
                            raise AssertionError('A refused client reached a controller')
                        await request(peer, '/secure', 401)
                        await request(peer, '/secure', 200, b'reader', headers=[(b'authorization', b'Bearer reader')])
                        _, pending = peer.start_request('/drain')
                        process.stdin.write(b'drain\n')
                        await process.stdin.drain()
                        fields, body = await asyncio.wait_for(pending, 8)
                        assert dict(fields)[b':status'] == b'200' and body == b'd' * 131072
                except ConnectionError:
                    if allowed:
                        raise
                    assert len(peers) == (2 if sibling else 1) and peers[-1].termination is not None
                    rejection = peers[-1].termination.error_code
                    expected = 0x174 if client == 'missing' else 0x12a
                    if client == 'mismatch':
                        expected = 0x133
                    if client == 'malformed' and os.name != 'nt':
                        expected = 0x132
                    assert rejection == expected, (label, rejection, expected)
                    if healthy is not None:
                        await request(healthy, '/health', 200, b'controller HTTP/3')
                        _, pending = healthy.start_request('/drain')
                        process.stdin.write(b'drain\n')
                        await process.stdin.drain()
                        fields, body = await asyncio.wait_for(pending, 8)
                        assert dict(fields)[b':status'] == b'200' and body == b'd' * 131072
                    else:
                        process.stdin.write(b'stop\n')
                        await process.stdin.drain()
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
    crl_server.close()
    await crl_server.wait_closed()
    (output / 'host.stdout.log').write_bytes(startup + stdout)
    (output / 'host.stderr.log').write_bytes(stderr)
    reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b'{')]
    report = reports[-1] if reports else {}
    validations = [json.loads(value) for value in report.get('clientCertificateValidations', [])]
    callbacks = (0 if client in ('missing', 'mismatch', 'malformed', 'unsupported-key') or mode in ('none', 'allow', 'require') else 1) + int(sibling)
    observations_ok = len(validations) == callbacks and all(value['chain'] is True
        and value['errors'] == 'RemoteCertificateChainErrors'
        and value['revocation'] == ('Online' if mode in ('require-online', 'require-custom-online') else 'NoCheck')
        and value['applicationPolicy'] == ['1.3.6.1.5.5.7.3.2'] for value in validations)
    revocation_ok = (bool(requests) and any('Revoked' in value.get('finalStatus', []) for value in validations)
                     if mode == 'require-custom-online' and client == 'revoked' else True)
    if mode not in ('require-online', 'require-custom-online'):
        revocation_ok = revocation_ok and not requests
    result = {'case': label, 'passed': failure is None and process.returncode == 0
              and report.get('completed') is True and report.get('rebind') is True
              and report.get('clientCertificateRequests') == (2 if allowed else 0)
              and report.get('secureRequests') == (1 if allowed else 0)
              and report.get('drainCompleted') == (allowed or sibling)
              and report.get('healthRequests') == int(sibling)
              and (allowed or rejection is not None) and observations_ok and revocation_ok
              and len(tickets) == (1 if mode == 'none' else 0)
              and report.get('retainedCertificates') == (2 if allowed and client != 'missing' and mode != 'none' else 0), 'failure': failure, 'rejection': rejection, 'healthySibling': sibling, 'crlRequests': requests,
              'sessionTickets': len(tickets), 'server': report, 'exit': process.returncode}
    (output / 'receipt.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result), flush=True)
    return result


async def main(args):
    lock = Path(__file__).resolve().parents[1] / 'EmbedIO.Conformance/drivers/requirements.txt'
    pins = [line.split('==', 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith('aioquic==')]
    if len(pins) != 1 or importlib.metadata.version('aioquic') != pins[0]:
        raise RuntimeError('Independent peer does not match the repository dependency lock')
    args.output.mkdir(parents=True, exist_ok=False)
    cases = [
        ('none', 'missing', True), ('none', 'client', True),
        ('allow', 'missing', True), ('allow', 'client', False),
        ('require', 'missing', False), ('require', 'client', False),
        ('allow-pin', 'missing', True), ('allow-pin', 'client', True),
        ('require-pin', 'client', True), ('require-pin', 'missing', False), ('require-pin', 'other', False),
        ('require-reject', 'client', False), ('require-throw', 'client', False),
        ('require-custom-root', 'client', True), ('require-custom-root', 'expired', False),
        ('require-custom-root', 'server-only', False), ('require-custom-root', 'missing-intermediate', False),
        ('require-pin', 'mismatch', False), ('require-pin', 'malformed', False),
        ('require-online', 'client', True), ('require-custom-online', 'client', True),
        ('require-custom-online', 'revoked', False), ('require-custom-root', 'revoked', True)]
    if os.name != 'nt':
        cases.append(('require-pin', 'unsupported-key', False))
    if args.profile == 'ci':
        cases = [('allow-pin', 'missing', True), ('require-pin', 'client', True),
                 ('require-pin', 'missing', False), ('require-custom-root', 'expired', False),
                 ('require-custom-online', 'client', True), ('require-custom-online', 'revoked', False)]
    results = []
    for mode, client, allowed in cases:
        results.append(await case(args, mode, client, allowed))
        if not results[-1]['passed']:
            break
    receipt = {**provenance(args), 'passed': len(results) == len(cases) and all(result['passed'] for result in results),
               'cases': results, 'scope': 'Library TLS client-certificate policy through actual WebServer/controller/auth/drain path; platform and native-library hashes are recorded above.'}
    (args.output / 'receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    return 0 if receipt['passed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dotnet', 'assembly'):
        parser.add_argument('--' + option, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--profile', choices=('full', 'ci'), default='full')
    args = parser.parse_args()
    args.openssl = 'schannel-rsa' if os.name == 'nt' else 'portable-rsa'
    raise SystemExit(asyncio.run(main(args)))
