"""Development-only capsule peers using pinned hyper-h2 or aioquic.

The conformance host owns the test-only example-tunnel extension. No UDP/TCP
forwarding, production Python dependency, or native QUIC datagram claim is made.
"""

import argparse
import asyncio
import importlib.metadata
import importlib.util
import json
from pathlib import Path
import sys
import time
import urllib.request


def load_peer(name):
    path = Path(__file__).with_name(name + "_campaign.py")
    spec = importlib.util.spec_from_file_location("capsule_peer_" + name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def integer(value, width=None):
    if value < 0 or value >= 1 << 62:
        raise ValueError("Capsule integer out of range")
    choices = (1, 2, 4, 8)
    minimum = next(size for size in choices if value < 1 << (8 * size - 2))
    width = minimum if width is None else width
    if width not in choices or width < minimum:
        raise ValueError("Invalid capsule integer width")
    return (value | (choices.index(width) << (8 * width - 2))).to_bytes(width, "big")


def capsule(kind, payload, wide=False):
    return integer(kind, 8 if wide else None) + integer(len(payload), 8 if wide else None) + payload


def vectors():
    payload = bytes((i * 7) & 255 for i in range(8192))
    yield "unknown-crosses-flow-window", capsule(23, bytes(196608)) + capsule(0, payload, True) + capsule(0, b""), capsule(0, payload) + capsule(0, b""), False
    yield "send-fin-preserves-input", capsule(0, b"abc"), capsule(0, b"abc"), False
    yield "truncated-type", b"\xc0\x00", b"", True
    yield "missing-length", b"\x00", b"", True
    yield "truncated-value", b"\x00\x0aabc", b"", True


def check_headers(state):
    fields = dict(state["headers"])
    if state["status"] != 200 or fields.get(b"capsule-protocol") != b"?1":
        raise AssertionError(f"Capsule carrier was not accepted: status={state['status']} headers={state['headers']} reset={state['reset']}")
    for name in (b"content-type", b"content-length", b"transfer-encoding"):
        if name in fields:
            raise AssertionError("Forbidden capsule carrier field: " + name.decode())


def stats(args):
    with urllib.request.urlopen(f"http://{args.host}:{args.stats_port}/__stats", timeout=5) as response:
        return json.load(response)


def check_final_input(args, before):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        after = stats(args)
        if after["capsuleAfterFinMessages"] == before["capsuleAfterFinMessages"] + 1:
            if after["capsuleAfterFinByteSum"] != before["capsuleAfterFinByteSum"] + 42:
                raise AssertionError("Final peer input was corrupted")
            if after["activeHandlers"] == 0:
                return
        time.sleep(0.02)
    raise AssertionError("Final peer input or handler completion was not observed")


def run_h2(args, vector):
    peer = load_peer("h2")
    name, wire, expected, malformed = vector
    half = name == "send-fin-preserves-input"
    before = stats(args) if half else None
    client = peer.Client(peer.Endpoint(args.host, args.port, args.tls, f"{args.host}:{args.port}".encode()))
    try:
        path = b"/capsule?half-close=true" if half else b"/capsule"
        sid = client.request(b"CONNECT", path, headers=[(b":protocol", b"example-tunnel"), (b"capsule-protocol", b"?1")], end=False)
        deadline = time.monotonic() + 5
        while client.streams[sid]["status"] is None and time.monotonic() < deadline:
            if not client.receive(0.05):
                raise AssertionError("Connection closed before capsule handshake")
        state = client.streams[sid]
        check_headers(state)
        # Upload through peer flow control, without an HTTP Content-Length field.
        state["pending"], state["end"] = wire, not half
        client.pump_uploads()
        client.flush()
        client.wait([sid])
        if malformed:
            if state["reset"] != ("server", 1):
                raise AssertionError(f"Expected PROTOCOL_ERROR, got {state['reset']}")
        elif state["reset"] is not None or not state["ended"] or bytes(state["body"]) != expected:
            raise AssertionError("Incorrect capsule echo or completion")
        if half:
            client.conn.send_data(sid, capsule(0, bytes([42])), end_stream=True)
            client.flush()
            check_final_input(args, before)
        healthy = client.request(b"GET", b"/plain")
        client.wait([healthy])
        live = client.streams[healthy]
        if live["status"] != 200 or bytes(live["body"]) != b"hello" or live["reset"] is not None or client.terminated is not None:
            raise AssertionError("Healthy sibling failed after capsule carrier")
    finally:
        client.close()


async def run_h3(args, vector):
    peer = load_peer("h3")
    name, wire, expected, malformed = vector
    half = name == "send-fin-preserves-input"
    before = stats(args) if half else None
    session = peer.Session(args.host, args.port)
    async with session.open() as client:
        sid = client._quic.get_next_available_stream_id()
        state = {"status": None, "headers": [], "body": bytearray(), "ended": False, "reset": None, "done": asyncio.Event()}
        client.streams[sid] = state
        path = b"/capsule?half-close=true" if half else b"/capsule"
        client.http.send_headers(sid, [(b":method", b"CONNECT"), (b":scheme", b"https"), (b":authority", session.authority),
                                      (b":path", path), (b":protocol", b"example-tunnel"), (b"capsule-protocol", b"?1")], end_stream=False)
        client.transmit()
        deadline = time.monotonic() + 5
        while state["status"] is None and time.monotonic() < deadline:
            await asyncio.sleep(0.01)
        check_headers(state)
        client.http.send_data(sid, wire, end_stream=not half)
        client.transmit()
        await client.wait([sid])
        if malformed:
            if state["reset"] != ("server", 0x10E):
                raise AssertionError(f"Expected H3_MESSAGE_ERROR, got {state['reset']}")
        elif state["reset"] is not None or not state["ended"] or bytes(state["body"]) != expected:
            raise AssertionError("Incorrect capsule echo or completion")
        if half:
            client.http.send_data(sid, capsule(0, bytes([42])), end_stream=True)
            client.transmit()
            await asyncio.to_thread(check_final_input, args, before)
        healthy = client.request(b"GET", b"/plain", session.authority)
        await client.wait([healthy])
        live = client.streams[healthy]
        if live["status"] != 200 or bytes(live["body"]) != b"hello" or live["reset"] is not None or client.terminated is not None:
            raise AssertionError("Healthy sibling failed after capsule carrier")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("protocol", choices=("h2", "h3"))
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--stats-port", type=int, required=True)
    parser.add_argument("--tls", action="store_true")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    results = []
    for vector in vectors():
        try:
            if args.protocol == "h2":
                run_h2(args, vector)
            else:
                asyncio.run(run_h3(args, vector))
            results.append({"name": vector[0], "result": "PASS"})
        except Exception as error:
            results.append({"name": vector[0], "result": "FAIL", "error": f"{type(error).__name__}: {error}"})
    report = {"protocol": args.protocol, "tls": args.tls, "peer": importlib.metadata.version("h2" if args.protocol == "h2" else "aioquic"),
              "outcomes": results, "result": "PASS" if all(item["result"] == "PASS" for item in results) else "FAIL"}
    Path(args.out).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())