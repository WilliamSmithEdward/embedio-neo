"""Regression checks for request registration around peer termination events."""
import asyncio
import datetime
import ssl
import unittest
from unittest.mock import patch
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID
from aioquic.quic.configuration import QuicConfiguration
from aioquic.quic.connection import QuicConnection
from aioquic.quic.events import ConnectionTerminated, StreamReset
from verify_tls_listener import CertificatePeer


class PassivePeer(CertificatePeer):
    def transmit(self):
        # These event-order checks use the actual QUIC/H3 objects without UDP.
        pass


class HttpPeerTests(unittest.IsolatedAsyncioTestCase):
    def peer(self):
        config = QuicConfiguration(is_client=True, alpn_protocols=['h3'])
        return PassivePeer(QuicConnection(configuration=config))

    async def test_termination_before_request_registration(self):
        peer = self.peer()
        event = ConnectionTerminated(error_code=0x12a, frame_type=6, reason_phrase='certificate')
        peer.quic_event_received(event)
        with self.assertRaisesRegex(ConnectionError, '298'):
            peer.start_request('/client')
        self.assertEqual(peer.responses, {})
        self.assertIs(peer.termination, event)

    async def test_termination_rejects_pending_and_later_requests(self):
        peer = self.peer()
        _, pending = peer.start_request('/client')
        peer.quic_event_received(ConnectionTerminated(error_code=0x133, frame_type=6, reason_phrase='proof'))
        with self.assertRaisesRegex(ConnectionError, '307'):
            await pending
        with self.assertRaisesRegex(ConnectionError, '307'):
            await peer.request('/health')

    async def test_authenticated_close_is_observed_before_draining_ends(self):
        key = ec.generate_private_key(ec.SECP256R1())
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'localhost')])
        now = datetime.datetime.now(datetime.timezone.utc)
        certificate = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(days=1))
            .not_valid_after(now + datetime.timedelta(days=1))
            .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
            .add_extension(x509.SubjectAlternativeName([x509.DNSName('localhost')]), critical=False)
            .sign(key, hashes.SHA256()))
        client_config = QuicConfiguration(is_client=True, alpn_protocols=['h3'], server_name='localhost', verify_mode=ssl.CERT_REQUIRED)
        client_config.load_verify_locations(cadata=certificate.public_bytes(serialization.Encoding.PEM))
        client = QuicConnection(configuration=client_config)
        peer = PassivePeer(client)
        server_address, client_address = ('127.0.0.1', 4433), ('127.0.0.1', 4434)
        peer.connect(server_address, transmit=False)
        server_config = QuicConfiguration(is_client=False, alpn_protocols=['h3'])
        server_config.certificate, server_config.private_key = certificate, key
        server = QuicConnection(configuration=server_config, original_destination_connection_id=client.original_destination_connection_id)
        loop = asyncio.get_running_loop()
        for _ in range(32):
            for data, _ in client.datagrams_to_send(loop.time()):
                server.receive_datagram(data, client_address, loop.time())
            for data, _ in server.datagrams_to_send(loop.time()):
                peer.datagram_received(data, server_address)
            if client._handshake_confirmed and server._handshake_complete:
                break
        self.assertTrue(client._handshake_confirmed and server._handshake_complete)
        _, response = peer.start_request('/client')
        server.close(error_code=0x12a, frame_type=6, reason_phrase='certificate')
        datagrams = server.datagrams_to_send(loop.time())
        self.assertEqual(len(datagrams), 1)
        data = datagrams[0][0]
        forged = data[:-1] + bytes([data[-1] ^ 1])
        peer.datagram_received(forged, server_address)
        self.assertFalse(response.done())
        self.assertIsNone(peer.termination)
        with patch.object(client._loss, 'get_probe_timeout', return_value=2.0):
            peer.datagram_received(data, server_address)
        self.assertGreater(client._close_at - loop.time(), 5.5)
        self.assertFalse(peer._closed.is_set(), 'Normal reference-peer drain cleanup must remain pending')
        self.assertTrue(response.done(), 'Authenticated close must end the HTTP wait before the drain timer')
        with self.assertRaisesRegex(ConnectionError, '298'):
            await response
        self.assertEqual(peer.termination.error_code, 0x12a)
        with self.assertRaisesRegex(ConnectionError, '298'):
            peer.start_request('/health')

    async def test_stream_reset_preserves_siblings_and_new_requests(self):
        peer = self.peer()
        stream, reset = peer.start_request('/client')
        _, sibling = peer.start_request('/health')
        peer.quic_event_received(StreamReset(error_code=0x10c, stream_id=stream))
        with self.assertRaises(ConnectionError):
            await reset
        self.assertFalse(sibling.done())
        _, later = peer.start_request('/health')
        self.assertFalse(later.done())
        sibling.cancel()
        later.cancel()
        self.assertIsNone(peer.termination)


if __name__ == '__main__':
    unittest.main()
