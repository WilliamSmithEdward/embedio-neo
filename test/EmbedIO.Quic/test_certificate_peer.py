"""Independent fixture checks for signed revocation responses and HTTP transport."""
import asyncio
import base64
from pathlib import Path
import tempfile
import unittest
from urllib.parse import quote
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509 import ocsp
from revocation_peer import serve_revocation
from verify_client_certificate import certificates


class RevocationPeerTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)
        _, self.resources = certificates(self.directory, 'http://127.0.0.1:12345/fixture')

    def tearDown(self):
        self.temporary.cleanup()

    def request(self, name='client', nonce=True):
        chain = x509.load_pem_x509_certificates((self.directory / (name + '.pem')).read_bytes())
        builder = ocsp.OCSPRequestBuilder().add_certificate(chain[0], chain[1], hashes.SHA256())
        if nonce:
            builder = builder.add_extension(x509.OCSPNonce(b'fixture-request-nonce'), False)
        return chain, builder.build()

    def check_response(self, encoded, chain, request, revoked=False, nonce=True):
        response = ocsp.load_der_ocsp_response(encoded)
        self.assertEqual(response.response_status, ocsp.OCSPResponseStatus.SUCCESSFUL)
        self.assertEqual(response.serial_number, request.serial_number)
        self.assertEqual(response.issuer_name_hash, request.issuer_name_hash)
        self.assertEqual(response.issuer_key_hash, request.issuer_key_hash)
        self.assertEqual(response.certificate_status, ocsp.OCSPCertStatus.REVOKED if revoked else ocsp.OCSPCertStatus.GOOD)
        chain[1].public_key().verify(response.signature, response.tbs_response_bytes, ec.ECDSA(response.signature_hash_algorithm))
        if nonce:
            self.assertEqual(response.extensions.get_extension_for_class(x509.OCSPNonce).value.nonce, b'fixture-request-nonce')
        else:
            self.assertEqual(len(response.extensions), 0)
        return response

    def test_signed_good_and_revoked_status_and_nonce(self):
        for name in ('client', 'revoked'):
            for nonce in (False, True):
                with self.subTest(name=name, nonce=nonce):
                    chain, request = self.request(name, nonce)
                    encoded = self.resources['/ocsp/' + name](request.public_bytes(serialization.Encoding.DER))
                    response = self.check_response(encoded, chain, request, name == 'revoked', nonce)
                    self.assertEqual(response.revocation_time_utc is not None, name == 'revoked')

    def test_wrong_certificate_and_malformed_requests_are_rejected(self):
        _, request = self.request('other')
        with self.assertRaises(ValueError):
            self.resources['/ocsp/client'](request.public_bytes(serialization.Encoding.DER))
        for encoded in (b'', b'invalid DER', b'x' * 4097):
            with self.subTest(length=len(encoded)), self.assertRaises(ValueError):
                self.resources['/ocsp/client'](encoded)

    async def exchange(self, request):
        requests = []
        async def serve(reader, writer):
            await serve_revocation(reader, writer, self.resources, requests)
        server = await asyncio.start_server(serve, '127.0.0.1', 0, limit=8192)
        try:
            reader, writer = await asyncio.open_connection('127.0.0.1', server.sockets[0].getsockname()[1])
            writer.write(request)
            await writer.drain()
            response = await asyncio.wait_for(reader.read(), 3)
            writer.close()
            await writer.wait_closed()
            return response, requests
        finally:
            server.close()
            await server.wait_closed()

    async def test_ocsp_over_get_and_post(self):
        chain, request = self.request()
        encoded = request.public_bytes(serialization.Encoding.DER)
        messages = [b'GET /ocsp/client/' + quote(base64.b64encode(encoded).decode(), safe='').encode() + b' HTTP/1.1\r\nHost: localhost\r\n\r\n',
                    b'POST /ocsp/client HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/ocsp-request\r\nContent-Length: ' + str(len(encoded)).encode() + b'\r\n\r\n' + encoded]
        for message in messages:
            response, requests = await self.exchange(message)
            header, body = response.split(b'\r\n\r\n', 1)
            self.assertTrue(header.startswith(b'HTTP/1.1 200 OK'))
            self.assertIn(b'Content-Type: application/ocsp-response', header)
            self.assertEqual(requests, ['/ocsp/client'])
            self.check_response(body, chain, request)

    async def test_crl_transport_and_oversized_body_rejection(self):
        response, requests = await self.exchange(b'GET /root.crl HTTP/1.1\r\nHost: localhost\r\n\r\n')
        header, body = response.split(b'\r\n\r\n', 1)
        self.assertIn(b'application/pkix-crl', header)
        self.assertEqual(body, self.resources['/root.crl'])
        self.assertEqual(requests, ['/root.crl'])
        response, requests = await self.exchange(b'POST /ocsp/client HTTP/1.1\r\nContent-Length: 4097\r\n\r\n')
        self.assertTrue(response.startswith(b'HTTP/1.1 400'))
        self.assertEqual(requests, [])


if __name__ == '__main__':
    unittest.main()
