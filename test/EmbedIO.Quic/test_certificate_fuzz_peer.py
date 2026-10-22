"""Keep the certificate exchange deadline independent of protocol close waiting."""
import asyncio
import unittest
from fuzz_client_certificate import certificate_exchange


class Peer:
    def __init__(self, stalled=False, rejected=False):
        self.stalled, self.rejected = stalled, rejected
        self.waited = False
        self.transmitted = False

    def transmit(self):
        self.transmitted = True

    async def wait_connected(self):
        if not self.transmitted:
            raise RuntimeError("The initial flight was not started")
        self.waited = True
        if self.rejected:
            raise ConnectionError("Rejected credential")
        if self.stalled:
            await asyncio.sleep(10)


class Connection:
    def __init__(self, peer, close_delay=0):
        self.peer, self.close_delay = peer, close_delay
        self.closed = False

    async def __aenter__(self):
        return self.peer

    async def __aexit__(self, *_):
        try:
            await asyncio.sleep(self.close_delay)
        finally:
            self.closed = True


class CertificateExchangeTests(unittest.IsolatedAsyncioTestCase):
    async def test_successful_request_does_not_spend_its_deadline_on_close(self):
        connection = Connection(Peer(), close_delay=0.2)
        async with asyncio.timeout(2):
            async with certificate_exchange(connection, timeout=0.05) as peer:
                self.assertTrue(peer.waited)
        self.assertTrue(connection.closed)

    async def test_handshake_stall_still_times_out_and_closes(self):
        connection = Connection(Peer(stalled=True))
        with self.assertRaises(TimeoutError):
            async with certificate_exchange(connection, timeout=0.05):
                self.fail("A stalled handshake was admitted")
        self.assertTrue(connection.closed)

    async def test_request_stall_still_times_out_and_closes(self):
        connection = Connection(Peer())
        with self.assertRaises(TimeoutError):
            async with certificate_exchange(connection, timeout=0.05):
                await asyncio.sleep(10)
        self.assertTrue(connection.closed)

    async def test_rejection_is_preserved_after_slow_close(self):
        connection = Connection(Peer(rejected=True), close_delay=0.2)
        with self.assertRaisesRegex(ConnectionError, "Rejected credential"):
            async with certificate_exchange(connection, timeout=0.05):
                self.fail("A rejected handshake was admitted")
        self.assertTrue(connection.closed)

    async def test_campaign_deadline_still_bounds_stalled_cleanup(self):
        connection = Connection(Peer(), close_delay=10)
        with self.assertRaises(TimeoutError):
            async with asyncio.timeout(0.1):
                async with certificate_exchange(connection, timeout=0.05):
                    pass
        self.assertTrue(connection.closed)


if __name__ == "__main__":
    unittest.main()
