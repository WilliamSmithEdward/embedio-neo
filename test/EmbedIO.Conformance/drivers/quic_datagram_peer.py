"""Development-only independent QUIC datagram peer (RFC 9221) using pinned aioquic.

Http3ApplicationDatagramPeerTest runs the http-echo mode against EmbedIO's HTTP/3
listener when EMBEDIO_DATAGRAM_PEER_PYTHON names a Python with
drivers/requirements.txt installed. The echo and unsupported modes exercise a
raw QUIC datagram server; no current test drives them. No production Python
dependency exists.

Modes:
  echo         advertise max_datagram_frame_size, expect the server greeting,
               then require every sent payload to come back byte-for-byte.
  unsupported  advertise no datagram support; the server must not send any.

One JSON object per line is written to stdout; the exit code is 0 on success.
"""

import argparse
import asyncio
import hashlib
import importlib.metadata
import json
import sys

from aioquic.asyncio import connect
from aioquic.asyncio.protocol import QuicConnectionProtocol
from aioquic.quic.configuration import QuicConfiguration
from aioquic.quic.events import DatagramFrameReceived
from aioquic.h3.connection import H3Connection
from aioquic.h3.events import DatagramReceived, HeadersReceived

GREETING = b"embedio-native-datagram-hello"
SIZES = (0, 1, 2, 63, 64, 255, 1000)
ATTEMPTS = 5


def report(**fields):
    print(json.dumps(fields, sort_keys=True), flush=True)


def payload(index, size):
    return bytes((index * 31 + i * 7) & 0xFF for i in range(size))


class Peer(QuicConnectionProtocol):
    def __init__(self, *args, http3=False, defer_http_settings=False, **kwargs):
        super().__init__(*args, **kwargs)
        self.datagrams = asyncio.Queue()
        # aioquic's public capability flag also emits SETTINGS_H3_DATAGRAM=1.
        # The test uses generic CONNECT; no WebTransport session is negotiated.
        self.deferred_stream_data = []
        send_stream_data = self._quic.send_stream_data
        if defer_http_settings:
            # Hold control frames after the stream-type preface, including SETTINGS.
            # QPACK prefaces must remain ordered before later decoder instructions.
            control_stream = None

            def defer_control(stream_id, data, end_stream=False):
                nonlocal control_stream
                if control_stream is None and data == b"\x00":
                    control_stream = stream_id
                elif stream_id == control_stream:
                    self.deferred_stream_data.append((stream_id, data, end_stream))
                    return
                send_stream_data(stream_id, data, end_stream=end_stream)

            self._quic.send_stream_data = defer_control
        try:
            self.http = H3Connection(self._quic, enable_webtransport=True) if http3 else None
        finally:
            self._quic.send_stream_data = send_stream_data
        self.headers = asyncio.Queue()

    def quic_event_received(self, event):
        if self.http is not None:
            for received in self.http.handle_event(event):
                if isinstance(received, HeadersReceived):
                    self.headers.put_nowait(received)
                elif isinstance(received, DatagramReceived):
                    self.datagrams.put_nowait((received.stream_id, received.data))
            return
        if isinstance(event, DatagramFrameReceived):
            self.datagrams.put_nowait(event.data)

    def send(self, data):
        self._quic.send_datagram_frame(data)
        self.transmit()


async def receive(peer, timeout):
    try:
        return await asyncio.wait_for(peer.datagrams.get(), timeout)
    except asyncio.TimeoutError:
        return None


async def echo(peer):
    greeting = await receive(peer, 10)
    if greeting != GREETING:
        raise AssertionError("expected server greeting, received %r" % (greeting,))
    report(event="greeting", length=len(greeting))
    retransmissions = 0
    for index, size in enumerate(SIZES):
        data = payload(index, size)
        for attempt in range(ATTEMPTS):
            peer.send(data)
            echoed = await receive(peer, 2)
            # Datagrams are unreliable: a lost request or reply is retried, but
            # anything that does arrive must match exactly.
            if echoed is not None and echoed != data:
                raise AssertionError("echo mismatch for %d bytes" % size)
            if echoed == data:
                break
            retransmissions += 1
        else:
            raise AssertionError("no echo for %d bytes after %d attempts" % (size, ATTEMPTS))
        report(event="echo", length=size, attempts=attempt + 1)
    report(event="echo-complete", count=len(SIZES), retransmissions=retransmissions)


async def unsupported(peer):
    stray = await receive(peer, 2)
    if stray is not None:
        raise AssertionError("server sent a datagram the peer never negotiated")
    report(event="no-datagrams")


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("echo", "unsupported", "http-echo"))
    parser.add_argument("--defer-http-settings", action="store_true")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--cafile", required=True)
    parser.add_argument("--sha256", required=True, help="expected leaf certificate SHA-256, hex")
    args = parser.parse_args()

    configuration = QuicConfiguration(
        is_client=True,
        alpn_protocols=["h3"],
        server_name="localhost",
        max_datagram_frame_size=65536 if args.mode != "unsupported" else None,
    )
    # Real chain verification against the generated test certificate.
    configuration.load_verify_locations(args.cafile)
    report(event="peer", aioquic=importlib.metadata.version("aioquic"), python=sys.version.split()[0])
    def protocol(*values, **options):
        return Peer(*values, http3=args.mode == "http-echo", defer_http_settings=args.defer_http_settings, **options)

    async with connect(args.host, args.port, configuration=configuration, create_protocol=protocol) as peer:
        await peer.wait_connected()
        leaf = hashlib.sha256(peer._quic.tls._peer_certificate.public_bytes(_der())).hexdigest()
        if leaf.lower() != args.sha256.lower():
            raise AssertionError("unexpected server certificate %s" % leaf)
        report(event="connected", remote_max_datagram_frame_size=peer._quic._remote_max_datagram_frame_size)
        if args.mode == "echo":
            if not peer._quic._remote_max_datagram_frame_size:
                raise AssertionError("server did not advertise max_datagram_frame_size")
            await echo(peer)
        elif args.mode == "http-echo":
            await http_echo(peer, args.port)
        else:
            await unsupported(peer)
        peer.close(error_code=0x100)
        await peer.wait_closed()
    report(event="done")


def _der():
    from cryptography.hazmat.primitives.serialization import Encoding
    return Encoding.DER


async def http_echo(peer, port):
    stream = peer._quic.get_next_available_stream_id()
    peer.http.send_headers(stream, [
        (b":method", b"CONNECT"), (b":scheme", b"https"),
        (b":authority", ("localhost:%s" % port).encode("ascii")),
        (b":path", b"/"), (b":protocol", b"neo-datagram-test"),
        (b"capsule-protocol", b"?1"),
    ])
    peer.transmit()
    response = await asyncio.wait_for(peer.headers.get(), 10)
    if response.stream_id != stream or dict(response.headers).get(b":status") != b"200":
        raise AssertionError("extension CONNECT was not accepted")
    if peer.deferred_stream_data:
        command = await asyncio.wait_for(asyncio.to_thread(sys.stdin.readline), 10)
        if command != "settings\n":
            raise AssertionError("expected explicit SETTINGS release from test host")
        for stream_id, data, end_stream in peer.deferred_stream_data:
            peer._quic.send_stream_data(stream_id, data, end_stream=end_stream)
        peer.deferred_stream_data.clear()
        peer.transmit()
        report(event="http-settings-released")
    if peer.http.received_settings.get(0x33) != 1:
        raise AssertionError("server did not negotiate HTTP datagrams")
    for index, size in enumerate(SIZES):
        expected = payload(index, size)
        for attempt in range(ATTEMPTS):
            peer.http.send_datagram(stream, expected)
            peer.transmit()
            echoed = await receive(peer, 1)
            if echoed == (stream, expected):
                break
            if echoed is not None:
                raise AssertionError("HTTP datagram was misrouted or changed")
        else:
            raise AssertionError("HTTP datagram echo did not arrive")
    report(event="http-echo-complete", count=len(SIZES), stream_id=stream)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as error:  # Reported to the NUnit host as a failure line.
        report(event="failure", error="%s: %s" % (type(error).__name__, error))
        sys.exit(1)
