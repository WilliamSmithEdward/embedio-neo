"""Seeded application-lifecycle campaigns over independent HTTP/2 and HTTP/3 peers.

Development-only. Each iteration opens one connection and mixes, on that connection:
interim plus body plus trailer responses (/lifecycle), uploads (/echo), capsule
tunnels (/capsule) including half-close, client resets at random points, and
malformed requests (Content-Length mismatch). Every stream the client did not reset
must complete exactly: interim statuses, final status, body bytes and the trailer
digest. Malformed requests must be reset as stream errors (RFC 9113 section 8.1.1,
PROTOCOL_ERROR; RFC 9114 section 4.1.2, H3_MESSAGE_ERROR) and never close the
connection. A sibling request must succeed afterwards. Resource settlement is read
from /__stats after a forced collection.

The drain mode opens in-flight lifecycle streams and a half-closed capsule tunnel,
starts a graceful drain through /__drain on a separate HTTP/1.1 endpoint, then checks
GOAWAY, completion of every admitted stream with its trailers, handling of a stream
opened after GOAWAY, peer input delivered to the tunnel during the drain, and the
drain outcome. HTTP/2 drain uses raw hyperframe frames and the hpack decoder, so it
does not depend on hyper-h2's connection state machine after GOAWAY.

Usage:
  python -I lifecycle_campaign.py h2 fuzz  --port P [--tls] --stats-port S --seed N --iterations N --out FILE
  python -I lifecycle_campaign.py h3 fuzz  --port P --stats-port S --seed N --iterations N --out FILE
  python -I lifecycle_campaign.py h2 drain --port P --tls --stats-port S --endpoint https --out FILE
  python -I lifecycle_campaign.py h3 drain --port P --stats-port S --endpoint h3 --out FILE

HTTP/3 uses aioquic with the same documented interim-section adapter as
response_sections.py: aioquic 1.3.0 treats every HEADERS block, including 103, as the
final header section. The adapter is recorded in every report.
"""

import argparse
import asyncio
import hashlib
import importlib.metadata
import importlib.util
import json
import random
import socket
import ssl
import sys
import time
from pathlib import Path

H3_REQUEST_CANCELLED = 0x10C
H3_MESSAGE_ERROR = 0x10E
H3_NO_ERROR = 0x100
# Drain observations, kept module-wide so a driver failure still reports them.
OBSERVATIONS = {}


def load(name):
    path = Path(__file__).with_name(name)
    spec = importlib.util.spec_from_file_location("lifecycle_" + path.stem, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def payload(size):
    return bytes(i % 251 for i in range(size))


def integer(value):
    for width in (1, 2, 4, 8):
        if value < 1 << (8 * width - 2):
            return (value | ((0, 1, 2, 3)[(1, 2, 4, 8).index(width)] << (8 * width - 2))).to_bytes(width, "big")
    raise ValueError("integer too large")


def capsule(kind, data):
    return integer(kind) + integer(len(data)) + data


def stats(host, port):
    with socket.create_connection((host, port), timeout=10) as sock:
        sock.sendall(f"GET /__stats HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: close\r\n\r\n".encode())
        data = b""
        while chunk := sock.recv(65536):
            data += chunk
    head, _, body = data.partition(b"\r\n\r\n")
    if b"transfer-encoding: chunked" in head.lower():
        decoded = b""
        while body:
            size_line, _, rest = body.partition(b"\r\n")
            size = int(size_line.split(b";")[0], 16)
            if size == 0:
                break
            decoded += rest[:size]
            body = rest[size + 2:]
        body = decoded
    return json.loads(body)


def settle(host, port):
    snapshot = stats(host, port)
    for _ in range(100):
        if snapshot["activeHandlers"] == 0:
            break
        time.sleep(0.1)
        snapshot = stats(host, port)
    return snapshot


def start_drain(host, port, endpoint, deadline_ms):
    with socket.create_connection((host, port), timeout=10) as sock:
        sock.sendall(f"POST /__drain?endpoint={endpoint}&ms={deadline_ms} HTTP/1.1\r\nHost: {host}:{port}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".encode())
        data = b""
        while chunk := sock.recv(65536):
            data += chunk
    if not data.startswith(b"HTTP/1.1 202"):
        raise AssertionError("drain was not started: " + data[:80].decode("latin-1"))


# ---------------------------------------------------------------- plan

def plan(rng):
    """One connection's operations. Each entry is (kind, parameters)."""
    operations = []
    for _ in range(rng.randint(1, 10)):
        roll = rng.random()
        if roll < 0.45:
            operations.append(("lifecycle", {
                "interim": rng.choice([0, 0, 1, 2]),
                "size": rng.choice([0, 3, 5000, 70000, 200000]),
                "chunk": rng.choice([1000, 16384, 65536]),
                "delay": rng.choice([0, 0, 0, 5]),
                "trailers": rng.choice([0, 1, 1]),
                "fixed": rng.choice([0, 1]),
            }))
        elif roll < 0.65:
            operations.append(("echo", {"body": rng.randbytes(rng.choice([0, 1, 5000, 70000, 300000]))}))
        elif roll < 0.80:
            operations.append(("capsule", {"data": rng.randbytes(rng.choice([0, 1, 300, 4000])), "half": rng.random() < 0.3}))
        elif roll < 0.90:
            operations.append(("malformed", {}))
        else:
            operations.append(("reset", {"after": rng.choice([0, 0.001, 0.01, 0.05])}))
    return operations


def lifecycle_path(p):
    return (f"/lifecycle?interim={p['interim']}&size={p['size']}&chunk={p['chunk']}"
            f"&delay={p['delay']}&trailers={p['trailers']}&fixed={p['fixed']}").encode()


def check(kind, p, state, protocol):
    """Raises AssertionError when a stream the client kept is not exactly right."""
    reset = state["reset"]
    if reset is not None and reset[0] == "client":
        return  # The client abandoned the stream first; nothing is owed on it.
    if kind == "malformed":
        expected = 1 if protocol == "h2" else H3_MESSAGE_ERROR
        if reset != ("server", expected):
            raise AssertionError(f"malformed request: expected stream error {expected:#x}, got reset={reset} status={state['status']}")
        return
    if reset is not None:
        raise AssertionError(f"{kind}: unexpected server reset {reset}")
    if not state["ended"]:
        raise AssertionError(f"{kind}: stream did not end")
    if kind == "lifecycle":
        if state["informational"] != [103] * p["interim"]:
            raise AssertionError(f"lifecycle: interim statuses {state['informational']} for {p}")
        if state["status"] != 200:
            raise AssertionError(f"lifecycle: status {state['status']}")
        body = payload(p["size"])
        if bytes(state["body"]) != body:
            raise AssertionError(f"lifecycle: {len(state['body'])} of {len(body)} bytes for {p}")
        trailers = {k.lower(): v for k, v in state.get("trailers") or []}
        headers = {k.lower(): v for k, v in state["headers"]}
        if b"x-lifecycle-sha256" in headers:
            raise AssertionError("lifecycle: trailer field leaked into the header section")
        if p["trailers"]:
            if trailers.get(b"x-lifecycle-sha256") != hashlib.sha256(body).hexdigest().upper().encode() or trailers.get(b"x-lifecycle-end") != b"done":
                raise AssertionError(f"lifecycle: trailers {trailers} for {p}")
        elif trailers:
            raise AssertionError(f"lifecycle: unexpected trailers {trailers}")
        if p["fixed"] and headers.get(b"content-length") != str(p["size"]).encode():
            raise AssertionError(f"lifecycle: content-length {headers.get(b'content-length')} for {p}")
    elif kind == "echo":
        if state["status"] != 200 or bytes(state["body"]) != p["body"]:
            raise AssertionError(f"echo: status {state['status']} {len(state['body'])} of {len(p['body'])} bytes")
    elif kind == "capsule":
        if state["status"] != 200:
            raise AssertionError(f"capsule: status {state['status']}")
        if bytes(state["body"]) != capsule(0, p["data"]):
            raise AssertionError(f"capsule: echo {len(state['body'])} bytes for {len(p['data'])}")


# ---------------------------------------------------------------- HTTP/2 fuzz

def h2_fuzz(args):
    peer = load("h2_campaign.py")
    import h2.events

    class Client(peer.Client):
        def handle(self, event):
            if isinstance(event, h2.events.TrailersReceived):
                self.streams[event.stream_id]["trailers"] = event.headers
            super().handle(event)

    rng = random.Random(args.seed)
    endpoint = peer.Endpoint(args.host, args.port, args.tls, f"{args.host}:{args.port}".encode())
    before = settle(args.host, args.stats_port)
    totals = dict(connections=0, streams=0, lifecycle=0, interim=0, trailers=0, echo=0, capsule=0, half_close=0, malformed=0, client_resets=0, server_resets=0)
    for iteration in range(args.iterations):
        operations = plan(rng)
        log = []
        client = Client(endpoint)
        totals["connections"] += 1
        try:
            opened = {}
            for kind, p in operations:
                if kind == "lifecycle":
                    sid = client.request(b"GET", lifecycle_path(p), headers=[(b"te", b"trailers")])
                elif kind == "echo":
                    sid = client.request(b"POST", b"/echo", p["body"], [(b"content-type", b"application/octet-stream")])
                elif kind == "capsule":
                    sid = client.request(b"CONNECT", b"/capsule?half-close=true" if p["half"] else b"/capsule",
                                         headers=[(b":protocol", b"example-tunnel"), (b"capsule-protocol", b"?1")], end=False)
                    state = client.streams[sid]
                    state["pending"], state["end"] = capsule(0, p["data"]), not p["half"]
                    client.pump_uploads()
                    client.flush()
                elif kind == "malformed":
                    sid = client.conn.get_next_available_stream_id()
                    client.conn.send_headers(sid, [(b":method", b"POST"), (b":scheme", b"https" if args.tls else b"http"),
                                                   (b":authority", endpoint.authority), (b":path", b"/echo"), (b"content-length", b"10")])
                    client.streams[sid] = {"status": None, "headers": [], "body": bytearray(), "ended": False, "reset": None,
                                           "pending": b"abc", "end": True, "informational": []}
                    client.pump_uploads()
                    client.flush()
                else:
                    live = [s for s in opened if not client.done(s)]
                    if live:
                        time.sleep(p["after"])
                        client.receive(0)
                        target = rng.choice(live)
                        if not client.done(target):
                            client.reset(target)
                            log.append(("reset", target))
                            totals["client_resets"] += 1
                    continue
                client.streams[sid].setdefault("trailers", [])
                opened[sid] = (kind, p)
                log.append((kind, sid))
                client.receive(0)
            client.wait(list(opened), 60)
            if client.terminated is not None:
                raise AssertionError(f"connection terminated: {client.terminated}")
            for sid, (kind, p) in opened.items():
                state = client.streams[sid]
                if kind == "capsule" and p["half"] and state["reset"] is None:
                    # Half-close mode: the server finished output after one echo while its
                    # input stays open. Ending the input completes the stream.
                    client.conn.end_stream(sid)
                    client.flush()
                check(kind, p, state, "h2")
                totals["streams"] += 1
                totals[kind] += 1
                if kind == "capsule" and p["half"]:
                    totals["half_close"] += 1
                if kind == "lifecycle" and state["reset"] is None:
                    totals["interim"] += len(state["informational"])
                    totals["trailers"] += 1 if state.get("trailers") else 0
                if state["reset"] and state["reset"][0] == "server":
                    totals["server_resets"] += 1
            sibling = client.request(b"GET", b"/plain")
            client.wait([sibling], 10)
            live = client.streams[sibling]
            if live["status"] != 200 or bytes(live["body"]) != b"hello" or client.terminated is not None:
                raise AssertionError(f"sibling failed: status {live['status']} reset {live['reset']} terminated {client.terminated}")
        except Exception as error:
            return {"result": "FAIL", "seed": args.seed, "iteration": iteration, "error": f"{type(error).__name__}: {error}",
                    "log": log, "plan": [(k, {a: (len(b) if isinstance(b, bytes) else b) for a, b in p.items()}) for k, p in operations],
                    "trace": client.trace[-60:], "totals": totals}
        finally:
            client.close()
    after = settle(args.host, args.stats_port)
    return finish(before, after, totals, args)


# ---------------------------------------------------------------- HTTP/3 fuzz

def h3_client_class():
    from aioquic.asyncio.protocol import QuicConnectionProtocol
    from aioquic.h3.connection import H3Connection, HeadersState
    from aioquic.h3.events import DataReceived, HeadersReceived
    from aioquic.quic.events import ConnectionTerminated, StreamReset

    class InterimAwareH3(H3Connection):
        # Same test-only adapter as response_sections.py; see the module docstring.
        def _handle_request_or_push_frame(self, frame_type, frame_data, stream, stream_ended):
            events = super()._handle_request_or_push_frame(frame_type, frame_data, stream, stream_ended)
            for event in events:
                if isinstance(event, HeadersReceived):
                    fields = dict(event.headers)
                    status = fields.get(b":status")
                    if status and int(status) < 200:
                        if int(status) == 101 or event.stream_ended or b"content-length" in fields:
                            raise AssertionError("Invalid informational section")
                        stream.headers_recv_state = HeadersState.INITIAL
            return events

    class Client(QuicConnectionProtocol):
        def __init__(self, *a, **kw):
            super().__init__(*a, **kw)
            self.http = InterimAwareH3(self._quic, enable_webtransport=False)
            self.streams = {}
            self.terminated = None

        def open(self, method, path, authority, headers=(), body=b"", end=True, content_length=None):
            sid = self._quic.get_next_available_stream_id()
            fields = [(b":method", method), (b":scheme", b"https"), (b":authority", authority), (b":path", path)] + list(headers)
            if content_length is not None:
                fields.append((b"content-length", str(content_length).encode()))
            self.streams[sid] = {"status": None, "headers": [], "body": bytearray(), "ended": False, "reset": None,
                                 "informational": [], "trailers": [], "done": asyncio.Event()}
            self.http.send_headers(sid, fields, end_stream=not body and end)
            if body:
                self.http.send_data(sid, body, end_stream=end)
            self.transmit()
            return sid

        def cancel(self, sid):
            state = self.streams[sid]
            if state["done"].is_set():
                return False
            self._quic.reset_stream(sid, H3_REQUEST_CANCELLED)
            self._quic.stop_stream(sid, H3_REQUEST_CANCELLED)
            state["reset"] = ("client", H3_REQUEST_CANCELLED)
            state["done"].set()
            self.transmit()
            return True

        def quic_event_received(self, event):
            if isinstance(event, StreamReset):
                state = self.streams.get(event.stream_id)
                if state and state["reset"] is None:
                    state["reset"] = ("server", event.error_code)
                    state["done"].set()
            elif isinstance(event, ConnectionTerminated):
                self.terminated = (event.error_code, event.reason_phrase)
                for state in self.streams.values():
                    state["done"].set()
            for item in self.http.handle_event(event):
                state = self.streams.get(getattr(item, "stream_id", -1))
                if state is None or state["reset"] is not None:
                    continue
                if isinstance(item, HeadersReceived):
                    status = dict(item.headers).get(b":status")
                    if status and int(status) < 200:
                        state["informational"].append(int(status))
                    elif status:
                        state["status"], state["headers"] = int(status), item.headers
                    else:
                        state["trailers"] = item.headers
                elif isinstance(item, DataReceived):
                    state["body"].extend(item.data)
                if getattr(item, "stream_ended", False):
                    state["ended"] = True
                    state["done"].set()

        async def wait(self, sids, timeout=60):
            await asyncio.wait_for(asyncio.gather(*(self.streams[s]["done"].wait() for s in sids)), timeout)

    return Client


def h3_configuration():
    from aioquic.h3.connection import H3_ALPN
    from aioquic.quic.configuration import QuicConfiguration
    config = QuicConfiguration(is_client=True, alpn_protocols=H3_ALPN, server_name="localhost")
    config.verify_mode = ssl.CERT_NONE  # The host generates a test certificate.
    config.idle_timeout = 30
    return config


async def h3_fuzz(args):
    from aioquic.asyncio.client import connect
    Client = h3_client_class()
    rng = random.Random(args.seed)
    authority = f"{args.host}:{args.port}".encode()
    before = settle(args.host, args.stats_port)
    totals = dict(connections=0, streams=0, lifecycle=0, interim=0, trailers=0, echo=0, capsule=0, half_close=0, malformed=0, client_resets=0, server_resets=0)
    for iteration in range(args.iterations):
        operations = plan(rng)
        log = []
        try:
            async with connect(args.host, args.port, configuration=h3_configuration(), create_protocol=Client, wait_connected=True) as client:
                totals["connections"] += 1
                opened = {}
                for kind, p in operations:
                    if kind == "lifecycle":
                        sid = client.open(b"GET", lifecycle_path(p), authority)
                    elif kind == "echo":
                        sid = client.open(b"POST", b"/echo", authority, [(b"content-type", b"application/octet-stream")], p["body"], True, len(p["body"]) if p["body"] else None)
                    elif kind == "capsule":
                        sid = client.open(b"CONNECT", b"/capsule?half-close=true" if p["half"] else b"/capsule", authority,
                                          [(b":protocol", b"example-tunnel"), (b"capsule-protocol", b"?1")], capsule(0, p["data"]), not p["half"])
                    elif kind == "malformed":
                        sid = client.open(b"POST", b"/echo", authority, [], b"abc", True, 10)
                    else:
                        live = [s for s in opened if not client.streams[s]["done"].is_set()]
                        if live:
                            await asyncio.sleep(p["after"])
                            target = rng.choice(live)
                            if client.cancel(target):
                                log.append(("reset", target))
                                totals["client_resets"] += 1
                        continue
                    opened[sid] = (kind, p)
                    log.append((kind, sid))
                    await asyncio.sleep(0)
                # Half-closed tunnels end only when the client also ends its input.
                for sid, (kind, p) in opened.items():
                    if kind == "capsule" and p["half"]:
                        await wait_status(client, sid)
                        if client.streams[sid]["reset"] is None:
                            client.http.send_data(sid, b"", end_stream=True)
                            client.transmit()
                await client.wait(list(opened), 60)
                if client.terminated is not None:
                    raise AssertionError(f"connection terminated: {client.terminated}")
                for sid, (kind, p) in opened.items():
                    state = client.streams[sid]
                    check(kind, p, state, "h3")
                    totals["streams"] += 1
                    totals[kind] += 1
                    if kind == "capsule" and p["half"]:
                        totals["half_close"] += 1
                    if kind == "lifecycle" and state["reset"] is None:
                        totals["interim"] += len(state["informational"])
                        totals["trailers"] += 1 if state["trailers"] else 0
                    if state["reset"] and state["reset"][0] == "server":
                        totals["server_resets"] += 1
                sibling = client.open(b"GET", b"/plain", authority)
                await client.wait([sibling], 10)
                live = client.streams[sibling]
                if live["status"] != 200 or bytes(live["body"]) != b"hello" or client.terminated is not None:
                    raise AssertionError(f"sibling failed: status {live['status']} reset {live['reset']} terminated {client.terminated}")
        except Exception as error:
            return {"result": "FAIL", "seed": args.seed, "iteration": iteration, "error": f"{type(error).__name__}: {error}",
                    "log": log, "plan": [(k, {a: (len(b) if isinstance(b, bytes) else b) for a, b in p.items()}) for k, p in operations], "totals": totals}
    after = settle(args.host, args.stats_port)
    return finish(before, after, totals, args)


async def wait_status(client, sid, timeout=10):
    deadline = time.monotonic() + timeout
    state = client.streams[sid]
    while state["status"] is None and state["reset"] is None and time.monotonic() < deadline:
        await asyncio.sleep(0.005)


def finish(before, after, totals, args):
    report = {"result": "PASS", "seed": args.seed, "iterations": args.iterations, "totals": totals,
              "stats_before": before, "stats_after": after,
              "handleGrowth": after["handles"] - before["handles"],
              "managedGrowth": after["managedBytes"] - before["managedBytes"],
              "threadGrowth": after["threads"] - before["threads"]}
    if after["activeHandlers"] or report["handleGrowth"] > 64 or report["managedGrowth"] > 32 << 20:
        report["result"] = "FAIL"
        report["error"] = "resources did not settle"
    return report


# ---------------------------------------------------------------- drain

def h2_drain(args):
    import hpack
    import hyperframe.frame as hf

    sock = socket.create_connection((args.host, args.port), timeout=15)
    if args.tls:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE  # The host generates a test certificate.
        context.set_alpn_protocols(["h2"])
        sock = context.wrap_socket(sock, server_hostname="localhost")
        if sock.selected_alpn_protocol() != "h2":
            raise AssertionError("missing h2 ALPN")
    encoder, decoder = hpack.Encoder(), hpack.Decoder()
    authority = f"localhost:{args.port}".encode()
    scheme = b"https" if args.tls else b"http"
    inbound = bytearray()
    observations = OBSERVATIONS
    observations["frames"] = []

    def send(frame):
        sock.sendall(frame.serialize())

    def headers(sid, fields, end):
        frame = hf.HeadersFrame(sid, encoder.encode(fields))
        frame.flags.add("END_HEADERS")
        if end:
            frame.flags.add("END_STREAM")
        send(frame)

    def data(sid, body, end):
        frame = hf.DataFrame(sid, body)
        if end:
            frame.flags.add("END_STREAM")
        send(frame)

    def receive(timeout):
        sock.settimeout(timeout)
        while True:
            if len(inbound) >= 9:
                length = int.from_bytes(inbound[:3], "big")
                if len(inbound) >= 9 + length:
                    frame, _ = hf.Frame.parse_frame_header(memoryview(bytes(inbound[:9])))
                    frame.parse_body(memoryview(bytes(inbound[9:9 + length])))
                    del inbound[:9 + length]
                    return frame
            try:
                chunk = sock.recv(65536)
            except TimeoutError:
                return "timeout"
            except (ConnectionResetError, ssl.SSLError, OSError):
                chunk = b""
            if not chunk:
                return None
            inbound.extend(chunk)

    sock.sendall(b"PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n")
    send(hf.SettingsFrame(0, settings={hf.SettingsFrame.INITIAL_WINDOW_SIZE: 1 << 20}))
    send(hf.WindowUpdateFrame(0, window_increment=16 << 20))
    streams = {}
    lifecycle = {"interim": 1, "size": 70000, "chunk": 7000, "delay": 60, "trailers": 1, "fixed": 0}
    for sid in (1, 3, 5):
        headers(sid, [(b":method", b"GET"), (b":scheme", scheme), (b":authority", authority), (b":path", lifecycle_path(lifecycle))], True)
        streams[sid] = {"informational": [], "status": None, "headers": [], "trailers": [], "body": bytearray(), "ended": False, "reset": None}
    tunnel_data = bytes(range(100))
    headers(7, [(b":method", b"CONNECT"), (b":scheme", scheme), (b":authority", authority), (b":path", b"/capsule?half-close=true"),
                (b":protocol", b"example-tunnel"), (b"capsule-protocol", b"?1")], False)
    data(7, capsule(0, tunnel_data), False)
    streams[7] = {"informational": [], "status": None, "headers": [], "trailers": [], "body": bytearray(), "ended": False, "reset": None}
    goaways = []
    late = []
    drain_started = None
    before_stats = stats(args.host, args.stats_port)
    deadline = time.monotonic() + 60
    late_sent = False
    tunnel_input_sent = False
    while time.monotonic() < deadline:
        frame = receive(10)
        if frame == "timeout":
            observations["silence"] = "no frame and no close for 10 s"
            break
        if frame is None:
            observations["eof"] = round(time.monotonic() - (drain_started or time.monotonic()), 3)
            break
        name = type(frame).__name__
        observations["frames"].append((name, frame.stream_id, sorted(frame.flags)))
        if isinstance(frame, hf.SettingsFrame) and "ACK" not in frame.flags:
            ack = hf.SettingsFrame(0)
            ack.flags.add("ACK")
            send(ack)
        elif isinstance(frame, hf.PingFrame) and "ACK" not in frame.flags:
            pong = hf.PingFrame(0, frame.opaque_data)
            pong.flags.add("ACK")
            send(pong)
        elif isinstance(frame, hf.GoAwayFrame):
            goaways.append((frame.last_stream_id, frame.error_code))
            if not late_sent and frame.last_stream_id != (1 << 31) - 1:
                headers(9, [(b":method", b"GET"), (b":scheme", scheme), (b":authority", authority), (b":path", b"/plain")], True)
                late_sent = True
        elif isinstance(frame, (hf.HeadersFrame, hf.ContinuationFrame)):
            if "END_HEADERS" not in frame.flags:
                raise AssertionError("CONTINUATION not expected for small sections")
            fields = decoder.decode(frame.data, raw=True)
            if frame.stream_id == 9:
                late.append("HEADERS")
                continue
            state = streams[frame.stream_id]
            status = dict(fields).get(b":status")
            if status and int(status) < 200:
                state["informational"].append(int(status))
            elif status:
                state["status"], state["headers"] = int(status), fields
            else:
                state["trailers"] = fields
            if "END_STREAM" in frame.flags:
                state["ended"] = True
        elif isinstance(frame, hf.DataFrame):
            if frame.flow_controlled_length:
                send(hf.WindowUpdateFrame(0, window_increment=frame.flow_controlled_length))
            if frame.stream_id == 9:
                late.append("DATA")
                continue
            streams[frame.stream_id]["body"].extend(frame.data)
            if "END_STREAM" in frame.flags:
                streams[frame.stream_id]["ended"] = True
        elif isinstance(frame, hf.RstStreamFrame):
            if frame.stream_id == 9:
                late.append(f"RST_STREAM {frame.error_code}")
            else:
                streams[frame.stream_id]["reset"] = frame.error_code
        # Start the drain once every lifecycle stream is mid-body and the tunnel echoed.
        if drain_started is None and all(streams[s]["status"] == 200 and streams[s]["body"] for s in (1, 3, 5)) and streams[7]["ended"]:
            start_drain(args.host, args.stats_port, args.endpoint, args.deadline_ms)
            drain_started = time.monotonic()
        # After GOAWAY, deliver more input to the half-closed tunnel and end it.
        if goaways and not tunnel_input_sent:
            data(7, capsule(0, bytes([42])), True)
            tunnel_input_sent = True
    after_stats = settle(args.host, args.stats_port)
    for _ in range(100):
        if after_stats.get("drainsCompleted", 0) > before_stats.get("drainsCompleted", 0) or after_stats.get("drainFailure"):
            break
        time.sleep(0.1)
        after_stats = stats(args.host, args.stats_port)
    return drain_report(args, streams, goaways, late, before_stats, after_stats, observations, lifecycle, tunnel_data, (1 << 31) - 1)


async def h3_drain(args):
    from aioquic.asyncio.client import connect
    from aioquic.h3.connection import H3Connection
    Client = h3_client_class()
    goaways = []

    # aioquic 1.3.0 does not surface GOAWAY to applications; record it from the
    # control stream frame handler without changing its processing.
    original = H3Connection._handle_control_frame

    def record_control(self, frame_type, frame_data):
        if frame_type == 0x7:
            from aioquic.buffer import Buffer
            goaways.append((Buffer(data=frame_data).pull_uint_var(), 0))
        return original(self, frame_type, frame_data)

    H3Connection._handle_control_frame = record_control
    authority = f"localhost:{args.port}".encode()
    lifecycle = {"interim": 1, "size": 70000, "chunk": 7000, "delay": 60, "trailers": 1, "fixed": 0}
    tunnel_data = bytes(range(100))
    observations = {}
    before_stats = stats(args.host, args.stats_port)
    late = []
    async with connect(args.host, args.port, configuration=h3_configuration(), create_protocol=Client, wait_connected=True) as client:
        sids = [client.open(b"GET", lifecycle_path(lifecycle), authority) for _ in range(3)]
        tunnel = client.open(b"CONNECT", b"/capsule?half-close=true", authority,
                             [(b":protocol", b"example-tunnel"), (b"capsule-protocol", b"?1")], capsule(0, tunnel_data), False)
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline and not (all(client.streams[s]["status"] == 200 and client.streams[s]["body"] for s in sids)
                                                    and client.streams[tunnel]["ended"]):
            await asyncio.sleep(0.005)
        await asyncio.to_thread(start_drain, args.host, args.stats_port, args.endpoint, args.deadline_ms)
        drain_started = time.monotonic()
        while not goaways and time.monotonic() < drain_started + 10:
            await asyncio.sleep(0.005)
        observations["goawayAfter"] = round(time.monotonic() - drain_started, 3) if goaways else None
        client.http.send_data(tunnel, capsule(0, bytes([42])), end_stream=True)
        client.transmit()
        # A request opened after GOAWAY with an ID at or above its value must not be processed.
        late_sid = client.open(b"GET", b"/plain", authority)
        await client.wait(sids, 30)
        try:
            await client.wait([late_sid], 10)
        except asyncio.TimeoutError:
            late.append("no response")
        state = client.streams[late_sid]
        if state["reset"]:
            late.append(f"reset {state['reset'][1]:#x}")
        elif state["status"] is not None:
            late.append(f"status {state['status']}")
        observations["lateStreamId"] = late_sid
        observations["completedAfter"] = round(time.monotonic() - drain_started, 3)
        streams = {s: client.streams[s] for s in sids}
        streams[tunnel] = client.streams[tunnel]
        await asyncio.sleep(0.5)
        observations["drainFinishedBeforeClientClose"] = stats(args.host, args.stats_port).get("drainsCompleted", 0) > before_stats.get("drainsCompleted", 0)
        client._quic.close(error_code=H3_NO_ERROR)
        client.transmit()
    after_stats = settle(args.host, args.stats_port)
    for _ in range(100):
        if after_stats.get("drainsCompleted", 0) > before_stats.get("drainsCompleted", 0) or after_stats.get("drainFailure"):
            break
        await asyncio.sleep(0.1)
        after_stats = stats(args.host, args.stats_port)
    H3Connection._handle_control_frame = original
    return drain_report(args, streams, goaways, late, before_stats, after_stats, observations, lifecycle, tunnel_data, None)


def drain_report(args, streams, goaways, late, before, after, observations, lifecycle, tunnel_data, sentinel):
    failures = []
    if not goaways:
        failures.append("no GOAWAY")
    elif any(code != 0 for _, code in goaways):
        failures.append(f"GOAWAY error codes {goaways}")
    final = [s for s, _ in goaways if s != sentinel]
    if final and any(sid >= final[-1] if args.protocol == "h3" else sid > final[-1] for sid in streams):
        failures.append(f"GOAWAY cutoff {final[-1]} excludes an admitted stream {sorted(streams)}")
    body = payload(lifecycle["size"])
    digest = hashlib.sha256(body).hexdigest().upper().encode()
    for sid, state in streams.items():
        is_tunnel = sid == max(streams)
        if state.get("reset"):
            failures.append(f"stream {sid} reset {state['reset']}")
            continue
        if not state["ended"]:
            failures.append(f"stream {sid} did not end")
            continue
        if is_tunnel:
            if bytes(state["body"]) != capsule(0, tunnel_data):
                failures.append("tunnel echo mismatch")
            continue
        trailers = {k.lower(): v for k, v in state["trailers"]}
        if state["informational"] != [103] or state["status"] != 200 or bytes(state["body"]) != body or trailers.get(b"x-lifecycle-sha256") != digest:
            failures.append(f"stream {sid} incomplete: interim {state['informational']} status {state['status']} body {len(state['body'])} trailers {sorted(trailers)}")
    if any(item in ("HEADERS", "DATA") or item.startswith("status") for item in late):
        failures.append(f"stream opened after GOAWAY was processed: {late}")
    if after.get("capsuleAfterFinMessages", 0) != before.get("capsuleAfterFinMessages", 0) + 1 or \
            after.get("capsuleAfterFinByteSum", 0) != before.get("capsuleAfterFinByteSum", 0) + 42:
        failures.append("tunnel input sent during drain was not delivered to the application")
    if after.get("drainFailure"):
        failures.append("drain failed: " + after["drainFailure"])
    elif after.get("drainsCompleted", 0) != before.get("drainsCompleted", 0) + 1:
        failures.append("drain did not complete")
    if after.get("activeHandlers"):
        failures.append(f"{after['activeHandlers']} handlers still active")
    observations.pop("frames", None) if not failures else None
    return {"result": "FAIL" if failures else "PASS", "failures": failures, "goaways": goaways, "lateStream": late or ["ignored"],
            "drainMilliseconds": after.get("drainMilliseconds"), "deadlineMilliseconds": args.deadline_ms,
            "observations": observations, "stats_before": before, "stats_after": after}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("protocol", choices=("h2", "h3"))
    parser.add_argument("mode", choices=("fuzz", "drain"))
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--stats-port", type=int, required=True)
    parser.add_argument("--tls", action="store_true")
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--iterations", type=int, default=100)
    parser.add_argument("--endpoint", default="https")
    parser.add_argument("--deadline-ms", type=int, default=15000)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    started = time.monotonic()
    try:
        if args.protocol == "h2":
            report = h2_fuzz(args) if args.mode == "fuzz" else h2_drain(args)
        else:
            report = asyncio.run(h3_fuzz(args) if args.mode == "fuzz" else h3_drain(args))
    except Exception as error:  # A driver failure is recorded with what was observed.
        import traceback
        report = {"result": "FAIL", "error": f"{type(error).__name__}: {error}", "traceback": traceback.format_exc(),
                  "observations": OBSERVATIONS}
    report["identity"] = {"protocol": args.protocol, "mode": args.mode, "tls": args.tls or args.protocol == "h3",
                          "h2": importlib.metadata.version("h2"), "hyperframe": importlib.metadata.version("hyperframe"),
                          "hpack": importlib.metadata.version("hpack"), "aioquic": importlib.metadata.version("aioquic"),
                          "h3InterimAdapter": args.protocol == "h3", "python": sys.version.split()[0],
                          "elapsedSeconds": round(time.monotonic() - started, 1)}
    Path(args.out).write_text(json.dumps(report, indent=1, ensure_ascii=True) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k not in ("trace", "observations")})[:4000])
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
