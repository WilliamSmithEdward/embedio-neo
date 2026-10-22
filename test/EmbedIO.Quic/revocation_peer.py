"""Bounded disposable CRL/OCSP responder for the independent TLS fixture."""
import asyncio
import base64
import datetime
from urllib.parse import unquote
from cryptography import x509
from cryptography.exceptions import UnsupportedAlgorithm
from cryptography.x509 import ocsp
from cryptography.hazmat.primitives import hashes, serialization


def make_ocsp_responder(certificate, issuer, signer, revoked=False):
    def respond(encoded):
        if not 1 <= len(encoded) <= 4096:
            raise ValueError("Invalid OCSP request length")
        request = ocsp.load_der_ocsp_request(encoded)
        expected = ocsp.OCSPRequestBuilder().add_certificate(certificate, issuer, request.hash_algorithm).build()
        if (request.serial_number != expected.serial_number
                or request.issuer_name_hash != expected.issuer_name_hash
                or request.issuer_key_hash != expected.issuer_key_hash):
            raise ValueError("OCSP request does not identify this fixture certificate")
        now = datetime.datetime.now(datetime.timezone.utc)
        builder = (ocsp.OCSPResponseBuilder().add_response(certificate, issuer, request.hash_algorithm,
            ocsp.OCSPCertStatus.REVOKED if revoked else ocsp.OCSPCertStatus.GOOD,
            now - datetime.timedelta(minutes=1), now + datetime.timedelta(hours=1),
            now - datetime.timedelta(seconds=30) if revoked else None,
            x509.ReasonFlags.key_compromise if revoked else None)
            .responder_id(ocsp.OCSPResponderEncoding.HASH, issuer).certificates([issuer]))
        for extension in request.extensions:
            if isinstance(extension.value, x509.OCSPNonce):
                builder = builder.add_extension(extension.value, critical=False)
            elif extension.critical:
                raise ValueError("Unsupported critical OCSP request extension")
        return builder.sign(signer, hashes.SHA256()).public_bytes(serialization.Encoding.DER)
    return respond


async def serve_revocation(reader, writer, resources, requests):
    try:
        header = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 3)
        lines = header.split(b"\r\n")
        first = lines[0].split()
        if len(first) != 3 or first[0] not in (b"GET", b"POST"):
            raise ValueError("Unsupported fixture HTTP request")
        path = first[1].decode("ascii")
        fields = {}
        for line in lines[1:]:
            if b":" in line:
                name, value = line.split(b":", 1)
                fields[name.strip().lower()] = value.strip()
        length = int(fields.get(b"content-length", b"0"))
        if not 0 <= length <= 4096 or b"transfer-encoding" in fields:
            raise ValueError("Invalid fixture request body length")
        encoded = await asyncio.wait_for(reader.readexactly(length), 3) if length else b""
        resource = resources.get(path)
        if resource is None and first[0] == b"GET":
            for name, candidate in resources.items():
                if callable(candidate) and path.startswith(name + "/"):
                    encoded = base64.b64decode(unquote(path[len(name) + 1:]), validate=True)
                    path, resource = name, candidate
                    break
        requests.append(path)
        if resource is None:
            status, content_type, body = b"404 Not Found", b"text/plain", b""
        else:
            body = resource(encoded) if callable(resource) else resource
            content_type = b"application/ocsp-response" if callable(resource) else b"application/pkix-crl"
            status = b"200 OK"
        writer.write(b"HTTP/1.1 " + status + b"\r\nContent-Type: " + content_type
            + b"\r\nConnection: close\r\nContent-Length: " + str(len(body)).encode() + b"\r\n\r\n" + body)
        await writer.drain()
    except (ValueError, UnsupportedAlgorithm):
        try:
            writer.write(b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            await writer.drain()
        except ConnectionError:
            pass
    except (ConnectionError, asyncio.TimeoutError, asyncio.IncompleteReadError, asyncio.LimitOverrunError):
        pass
    finally:
        writer.close()
        try:
            await writer.wait_closed()
        except ConnectionError:
            pass
