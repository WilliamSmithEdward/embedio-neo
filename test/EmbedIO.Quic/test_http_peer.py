"""Regression checks for request registration around peer termination events."""
import unittest
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
