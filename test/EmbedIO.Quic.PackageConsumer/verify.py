"""Exercise published package assets over real HTTP/3, including Native AOT."""
import argparse
import asyncio
import datetime
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import sys
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID
from aioquic.asyncio import connect
from aioquic.tls import CipherSuite
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "EmbedIO.Quic"))
from verify_http3 import HttpPeer
from verify_tls_listener import configuration


def certificate(directory):
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
    now = datetime.datetime.now(datetime.timezone.utc)
    value = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
             .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(minutes=5))
             .not_valid_after(now + datetime.timedelta(minutes=30))
             .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
             .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost")]), critical=False)
             .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=True)
             .add_extension(x509.KeyUsage(True, False, False, False, False, False, False, None, None), critical=True)
             .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False).sign(key, hashes.SHA256()))
    cert_path, key_path = directory / "certificate.pem", directory / "key.pem"
    cert_path.write_bytes(value.public_bytes(serialization.Encoding.PEM))
    key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    key_path.chmod(0o600)
    return cert_path, key_path


async def main(args):
    requirements = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    expected = [line.split("==", 1)[1].split()[0] for line in requirements.read_text(encoding="utf-8").splitlines() if line.startswith("aioquic==")]
    if len(expected) != 1 or importlib.metadata.version("aioquic") != expected[0]:
        raise RuntimeError("Independent peer does not match the dependency lock")
    args.output.mkdir(parents=True, exist_ok=False)
    cert_path, key_path = certificate(args.output)
    command = args.launcher + ([str(args.entry)] if args.native else [args.dotnet, str(args.entry)]) + [str(cert_path), str(key_path)]
    if args.missing_tls:
        command.append("--missing-tls")
    process = await asyncio.create_subprocess_exec(*command, stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    if args.missing_tls:
        try:
            stdout, stderr = await asyncio.wait_for(process.communicate(), 15)
        except BaseException:
            if process.returncode is None:
                process.kill()
                await process.wait()
            raise
        (args.output / "stdout.log").write_bytes(stdout)
        (args.output / "stderr.log").write_bytes(stderr)
        reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
        if process.returncode != 0 or reports != [{"runtimeMissing": True}]:
            raise AssertionError("Missing private TLS runtime did not fail at public server construction")
        (args.output / "result.json").write_text(json.dumps({"passed": True, "missingRuntimeRejected": True}) + "\n")
        return
    startup, stdout, stderr = bytearray(), b"", b""
    failure, ready, requests = None, {}, 0
    try:
        async with asyncio.timeout(10):
            while "port" not in ready:
                line = await process.stdout.readline()
                startup.extend(line)
                if not line or len(startup) > 32768:
                    raise RuntimeError("Consumer ended or exceeded its startup output limit")
                if line.startswith(b"{"):
                    ready = json.loads(line)
        config = configuration(cert_path, "localhost")
        config.cipher_suites = [CipherSuite[args.cipher]]
        async with asyncio.timeout(15):
            async with connect("127.0.0.1", ready["port"], configuration=config, create_protocol=HttpPeer) as peer:
                peer.authority = f"127.0.0.1:{ready['port']}".encode()
                if peer._quic.tls.key_schedule.cipher_suite != CipherSuite[args.cipher]:
                    raise AssertionError("Requested cipher was not negotiated")
                async def request(path, status, body=None, data=None, headers=()):
                    nonlocal requests
                    _, fields, actual = await peer.request(path, data=data, headers=headers)
                    if fields[b":status"] != str(status).encode() or body is not None and actual != body:
                        raise AssertionError((path, fields, actual[:100]))
                    requests += 1
                    return actual
                await request("/health", 200, b"package HTTP/3")
                await request("/secure", 401)
                await request("/secure", 200, b"reader", headers=[(b"authorization", b"Bearer reader")])
                payload = bytes(index % 251 for index in range(131072))
                await request("/echo", 200, payload, data=payload)
                await request("/echo", 413, data=payload + b"x")
                stream, pending = peer.start_request("/abort")
                async with asyncio.timeout(3):
                    while bytes(peer.responses[stream][2]) != b"r":
                        await asyncio.sleep(0.01)
                peer._quic.stop_stream(stream, 0x10c)
                peer._quic.reset_stream(stream, 0x10c)
                peer.transmit()
                pending.cancel()
                async with asyncio.timeout(3):
                    while await request("/abort-status", 200) != b"1":
                        await asyncio.sleep(0.01)
                await request("/health", 200, b"package HTTP/3")
                await peer.ping()
        process.stdin.write(b"stop\n")
        await process.stdin.drain()
        stdout, stderr = await asyncio.wait_for(process.communicate(), 8)
        reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
        if process.returncode != 0 or not reports or reports[-1] != dict(passed=True, aborted=1, contexts=0, blocks=0):
            raise AssertionError((process.returncode, reports, stderr.decode("utf-8", "replace")))
    except Exception as error:
        failure = repr(error)
        try:
            if process.returncode is None:
                process.stdin.write(b"stop\n")
                await process.stdin.drain()
            stdout, stderr = await asyncio.wait_for(process.communicate(), 8)
        except (ConnectionError, asyncio.TimeoutError):
            if process.returncode is None:
                process.kill()
            stdout, stderr = await process.communicate()
    (args.output / "host.stdout.log").write_bytes(startup + stdout)
    (args.output / "host.stderr.log").write_bytes(stderr)
    result = dict(passed=failure is None, failure=failure, cipher=args.cipher, nativeAot=args.native,
                  launcher=args.launcher, entrySha256=hashlib.sha256(args.entry.read_bytes()).hexdigest(), requests=requests,
                  peerVersion=expected[0], exit=process.returncode,
                  scope="Package-only references, verified TLS/cipher, generated controllers, bearer policy, bounded echo/rejection, request-abort propagation, healthy subsequent requests and owned cleanup.")
    (args.output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result), flush=True)
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--entry", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--native", action="store_true")
    parser.add_argument("--launcher", type=json.loads, default=[],
                        help="JSON array of arguments placed before the server command, such as a container run")
    parser.add_argument("--missing-tls", action="store_true")
    parser.add_argument("--cipher", choices=("AES_128_GCM_SHA256", "AES_256_GCM_SHA384", "CHACHA20_POLY1305_SHA256"), default="AES_128_GCM_SHA256")
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    if not isinstance(arguments.launcher, list) or not all(isinstance(item, str) for item in arguments.launcher):
        parser.error("--launcher must be a JSON array of strings")
    raise SystemExit(asyncio.run(main(arguments)))
