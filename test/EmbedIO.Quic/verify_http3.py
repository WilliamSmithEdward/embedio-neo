"""Independent HTTP/3 checks through the library protocol and replacement QUIC."""
import argparse
import asyncio
import json
import importlib.metadata
import ssl
from pathlib import Path
from aioquic.asyncio import connect
from aioquic.asyncio.protocol import QuicConnectionProtocol
from aioquic.quic.configuration import QuicConfiguration
from aioquic.tls import CipherSuite
from aioquic.quic.events import ConnectionTerminated, StreamReset, StopSendingReceived
from aioquic.h3.connection import H3Connection
from aioquic.h3.events import HeadersReceived, DataReceived
from verify import provenance
from verify_connections import run


class HttpPeer(QuicConnectionProtocol):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.http = H3Connection(self._quic)
        self.responses = {}
        self.stopped = {}
        self._connection_error = None

    def quic_event_received(self, event):
        if isinstance(event, ConnectionTerminated):
            self._connection_error = repr(event)
        if isinstance(event, StopSendingReceived):
            self.stopped[event.stream_id] = event.error_code
        if isinstance(event, (ConnectionTerminated, StreamReset)):
            for stream, (future, _, _) in self.responses.items():
                if not future.done() and (isinstance(event, ConnectionTerminated) or stream == event.stream_id):
                    future.set_exception(ConnectionError(repr(event)))
        for received in self.http.handle_event(event):
            if isinstance(received, (HeadersReceived, DataReceived)):
                future, headers, body = self.responses[received.stream_id]
                if isinstance(received, HeadersReceived):
                    headers.extend(received.headers)
                else:
                    body.extend(received.data)
                if received.stream_ended and not future.done():
                    future.set_result((headers, bytes(body)))

    def start_request(self, path, data=None, incomplete=False, headers=()):
        if self._connection_error is not None:
            raise ConnectionError(self._connection_error)
        stream = self._quic.get_next_available_stream_id()
        future = asyncio.get_running_loop().create_future()
        self.responses[stream] = (future, [], bytearray())
        fields = [(b':method', b'GET' if data is None else b'POST'), (b':scheme', b'https'),
                  (b':authority', getattr(self, 'authority', b'localhost')), (b':path', path.encode())]
        fields.extend(headers)
        if data is not None:
            fields.append((b'content-length', str(len(data) + (1000000 if incomplete else 0)).encode()))
        self.http.send_headers(stream, fields, end_stream=data is None)
        if data is not None:
            self.http.send_data(stream, data, end_stream=not incomplete)
        self.transmit()
        return stream, future

    async def request(self, path, data=None, incomplete=False, headers=()):
        stream, future = self.start_request(path, data, incomplete, headers)
        headers, body = await asyncio.wait_for(future, 5)
        return stream, dict(headers), body


async def client(args, port, index, barrier):
    config = QuicConfiguration(is_client=True, alpn_protocols=['h3'], server_name='localhost',
                               verify_mode=ssl.CERT_REQUIRED, idle_timeout=5, max_datagram_frame_size=65536)
    if getattr(args, 'cipher', None):
        config.cipher_suites = [CipherSuite[args.cipher]]
    config.load_verify_locations(cafile=str(args.output / 'certificate.pem'))
    async with connect('127.0.0.1', port, configuration=config, create_protocol=HttpPeer) as peer:
        if getattr(args, 'cipher', None):
            assert peer._quic.tls.key_schedule.cipher_suite == CipherSuite[args.cipher]
        await barrier.wait()
        _, headers, body = await peer.request('/health')
        assert headers[b':status'] == b'200' and body == b'production HTTP/3'
        payload = bytes((value + index) % 251 for value in range(131072))
        _, headers, body = await peer.request('/echo', payload)
        assert headers[b':status'] == b'200' and body == payload
        stream, headers, body = await peer.request('/timeout', b'incomplete', incomplete=True)
        assert headers[b':status'] == b'408' and body == b'complete timeout response'
        await peer.ping()
        assert peer.stopped.get(stream) == 0x100, peer.stopped
        results = await asyncio.gather(*(peer.request('/health') for _ in range(4)))
        assert all(headers[b':status'] == b'200' and body == b'production HTTP/3' for _, headers, body in results)


async def main(args):
    lock = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    pins = [line.split("==", 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith("aioquic==")]
    if len(pins) != 1 or importlib.metadata.version("aioquic") != pins[0]:
        raise RuntimeError("Independent peer does not match the repository dependency lock")
    args.output.mkdir(parents=True, exist_ok=False)
    process = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, 'certificate', str(args.output))
    if await process.wait() != 0:
        raise RuntimeError('Certificate creation failed')
    count = 4
    async def operation(ready):
        barrier = asyncio.Barrier(count)
        async with asyncio.TaskGroup() as group:
            for index in range(count):
                group.create_task(client(args, ready['port'], index, barrier))
    result = await run(args, 'production-http3', 'serve-many-production-h3', count, operation)
    receipt = {**provenance(args), 'cases': [result], 'clients': count, 'requestsPerClient': 7,
               'scope': 'Actual library HTTP/3 protocol over EmbedIO.Quic; development TLS and UDP host; controller/listener configuration not covered.'}
    (args.output / 'receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--assembly', required=True)
    parser.add_argument('--openssl', required=True)
    parser.add_argument('--cipher', choices=('AES_128_GCM_SHA256', 'AES_256_GCM_SHA384', 'CHACHA20_POLY1305_SHA256'))
    parser.add_argument('--output', type=Path, required=True)
    raise SystemExit(asyncio.run(main(parser.parse_args())))
