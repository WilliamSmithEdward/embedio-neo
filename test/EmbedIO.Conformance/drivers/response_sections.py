"""Development-only independent HTTP/1, hyper-h2 and aioquic response peers."""
import argparse
import asyncio
import base64
import hashlib
import importlib.metadata
import json
import socket
import ssl
from pathlib import Path


def payload(size):
    return bytes(i % 251 for i in range(size))


def verify(state, size, healthy=False):
    expected = b"hello" if healthy else payload(size)
    statuses = [200] if healthy else [103, 103, 200]
    if state["statuses"] != statuses or bytes(state["body"]) != expected or not state["ended"]:
        raise AssertionError(f"Wrong sections/body/end: {state['statuses']} {len(state['body'])} {state['ended']}")
    if not healthy:
        digest = b"sha-256=:" + base64.b64encode(hashlib.sha256(expected).digest()) + b":"
        if dict(state["trailers"]).get(b"content-digest") != digest:
            raise AssertionError("Missing or incorrect digest trailer")
        if dict(state["trailers"]).get(b"x-section-end") != b"finished":
            raise AssertionError("Missing ending trailer")
        if b"content-digest" in dict(state["headers"]):
            raise AssertionError("Trailer leaked into final headers")
        if state["hints"] != [b"</one>; rel=preload", b"</two>; rel=preload"]:
            raise AssertionError("Interim sections changed order or content")


def state():
    return dict(statuses=[], hints=[], headers=[], trailers=[], body=bytearray(), ended=False)


def fields(stream):
    result = []
    while True:
        line = stream.readline(32769)
        if line == b"\r\n": return result
        if not line or not line.endswith(b"\r\n") or b":" not in line:
            raise AssertionError("Incomplete field section")
        name, value = line[:-2].split(b":", 1)
        result.append((name.lower(), value.strip()))


def h1(args):
    sock = socket.create_connection((args.host, args.port), timeout=15)
    if args.tls:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.load_verify_locations(args.cert)
        sock = context.wrap_socket(sock, server_hostname="localhost")
    with sock, sock.makefile("rb") as stream:
        for size in (0, 3, 196608):
            for path, healthy in ((f"/sections?size={size}", False), ("/plain", True)):
                sock.sendall(f"GET {path} HTTP/1.1\r\nHost: localhost:{args.port}\r\nTE: trailers\r\n\r\n".encode())
                current = state()
                while True:
                    head = stream.readline(32769).split()
                    if len(head) < 2: raise AssertionError("Missing response head")
                    status = int(head[1]); current["statuses"].append(status)
                    block = fields(stream)
                    if status >= 200:
                        current["headers"] = block; break
                    current["hints"].append(dict(block).get(b"link"))
                header = dict(block)
                if header.get(b"transfer-encoding") == b"chunked":
                    while True:
                        count = int(stream.readline().split(b";", 1)[0], 16)
                        if count == 0:
                            current["trailers"] = fields(stream); break
                        data = stream.read(count)
                        if len(data) != count or stream.read(2) != b"\r\n": raise AssertionError("Truncated chunk")
                        current["body"].extend(data)
                elif b"content-length" in header:
                    current["body"].extend(stream.read(int(header[b"content-length"])))
                else: raise AssertionError("Unbounded response framing")
                current["ended"] = True; verify(current, size, healthy)
            print(f"PASS h1 size={size}")


def h2(args):
    from h2.connection import H2Connection
    from h2.config import H2Configuration
    from h2.events import InformationalResponseReceived, ResponseReceived, TrailersReceived, DataReceived, StreamEnded, StreamReset, ConnectionTerminated
    sock = socket.create_connection((args.host, args.port), timeout=15)
    if args.tls:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.load_verify_locations(args.cert); context.set_alpn_protocols(["h2"])
        sock = context.wrap_socket(sock, server_hostname="localhost")
        if sock.selected_alpn_protocol() != "h2": raise AssertionError("Missing h2 ALPN")
    conn = H2Connection(config=H2Configuration(client_side=True)); conn.initiate_connection()
    with sock:
        sock.sendall(conn.data_to_send())
        for size in (0, 3, 196608):
            for fixed in (0, 1):
                for path, healthy in ((f"/sections?size={size}&fixed={fixed}", False), ("/plain", True)):
                    sid = conn.get_next_available_stream_id(); current = state()
                    conn.send_headers(sid, [(b":method", b"GET"), (b":scheme", b"https" if args.tls else b"http"),
                        (b":authority", f"localhost:{args.port}".encode()), (b":path", path.encode())], end_stream=True)
                    sock.sendall(conn.data_to_send())
                    while not current["ended"]:
                        data = sock.recv(65536)
                        if not data: raise AssertionError("Early connection EOF")
                        for event in conn.receive_data(data):
                            if isinstance(event, (StreamReset, ConnectionTerminated)): raise AssertionError(str(event))
                            if getattr(event, "stream_id", -1) != sid: continue
                            if isinstance(event, InformationalResponseReceived):
                                current["statuses"].append(int(dict(event.headers)[b":status"]))
                                current["hints"].append(dict(event.headers).get(b"link"))
                            elif isinstance(event, ResponseReceived):
                                current["statuses"].append(int(dict(event.headers)[b":status"])); current["headers"] = event.headers
                            elif isinstance(event, TrailersReceived): current["trailers"] = event.headers
                            elif isinstance(event, DataReceived):
                                current["body"].extend(event.data); conn.acknowledge_received_data(event.flow_controlled_length, sid)
                            elif isinstance(event, StreamEnded): current["ended"] = True
                        outgoing = conn.data_to_send()
                        if outgoing: sock.sendall(outgoing)
                    verify(current, size, healthy)
                print(f"PASS h2 size={size} fixed={fixed}")


async def h3(args):
    from aioquic.asyncio.client import connect
    from aioquic.asyncio.protocol import QuicConnectionProtocol
    from aioquic.h3.connection import H3Connection, H3_ALPN
    from aioquic.h3.events import HeadersReceived, DataReceived
    from aioquic.quic.configuration import QuicConfiguration
    from aioquic.quic.events import ConnectionTerminated, StreamReset
    class Peer(QuicConnectionProtocol):
        def __init__(self, *a, **kw):
            super().__init__(*a, **kw); self.http = H3Connection(self._quic); self.states = {}
        def quic_event_received(self, event):
            if isinstance(event, (ConnectionTerminated, StreamReset)):
                for current in self.states.values():
                    current["error"] = str(event); current["done"].set()
            for item in self.http.handle_event(event):
                current = self.states.get(getattr(item, "stream_id", -1))
                if current is None: continue
                if isinstance(item, HeadersReceived):
                    block = dict(item.headers); status = block.get(b":status")
                    if status:
                        number = int(status); current["statuses"].append(number)
                        if number < 200: current["hints"].append(block.get(b"link"))
                        else: current["headers"] = item.headers
                    else: current["trailers"] = item.headers
                elif isinstance(item, DataReceived): current["body"].extend(item.data)
                if getattr(item, "stream_ended", False): current["ended"] = True; current["done"].set()
        async def get(self, path):
            sid = self._quic.get_next_available_stream_id(); current = state()
            current.update(done=asyncio.Event(), error=None); self.states[sid] = current
            self.http.send_headers(sid, [(b":method", b"GET"), (b":scheme", b"https"),
                (b":authority", f"localhost:{args.port}".encode()), (b":path", path.encode())], end_stream=True)
            self.transmit(); await asyncio.wait_for(current["done"].wait(), 15)
            if current["error"]: raise AssertionError(current["error"])
            return current
    config = QuicConfiguration(is_client=True, alpn_protocols=H3_ALPN)
    config.load_verify_locations(args.cert)
    async with connect("localhost", args.port, configuration=config, create_protocol=Peer) as peer:
        for size in (0, 3, 196608):
            for fixed in (0, 1):
                verify(await peer.get(f"/sections?size={size}&fixed={fixed}"), size)
                verify(await peer.get("/plain"), size, True)
                print(f"PASS h3 size={size} fixed={fixed}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("protocol", choices=("h1", "h2", "h3"))
    parser.add_argument("--host", default="127.0.0.1"); parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--tls", action="store_true"); parser.add_argument("--cert", required=True)
    args = parser.parse_args()
    print(json.dumps({"protocol": args.protocol, "h2": importlib.metadata.version("h2"), "aioquic": importlib.metadata.version("aioquic")}))
    if args.protocol == "h3": asyncio.run(h3(args))
    elif args.protocol == "h2": h2(args)
    else: h1(args)
