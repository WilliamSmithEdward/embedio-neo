"""Independent aioquic client for the isolated QUIC feasibility executable.

Uses only the peer's public connection/stream APIs. Each case gets a fresh server,
normal certificate verification, an absolute deadline and retained process logs.
"""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import importlib.metadata
import json
from pathlib import Path
import socket
import ssl
import subprocess

from aioquic.asyncio import connect
from aioquic.asyncio.protocol import QuicConnectionProtocol
from aioquic.quic.configuration import QuicConfiguration
from aioquic.quic.events import HandshakeCompleted, ConnectionTerminated

ALPN = "embedio-quic-probe"


class Peer(QuicConnectionProtocol):
    negotiated_alpn = None
    termination = None

    def quic_event_received(self, event):
        if isinstance(event, HandshakeCompleted):
            self.negotiated_alpn = event.alpn_protocol
        if isinstance(event, ConnectionTerminated):
            self.termination = {"code": event.error_code, "reason": event.reason_phrase}
        super().quic_event_received(event)


class DatagramRelay(asyncio.DatagramProtocol):
    """Mutates wire packets without inspecting either QUIC implementation."""
    def __init__(self, server):
        self.server = server
        self.client = None
        self.transport = None
        self.injected = False

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, address):
        if address == self.server:
            if self.client is not None:
                self.transport.sendto(data, self.client)
            return
        if self.client is None:
            self.client = address
        if address != self.client:
            return
        if data and not data[0] & 0x80 and not self.injected:
            self.injected = True
            self.transport.sendto(data[:5], self.server)
            altered = bytearray(data)
            altered[-1] ^= 1
            self.transport.sendto(altered, self.server)
            self.transport.sendto(data, self.server)
        self.transport.sendto(data, self.server)


async def case(args, name, payload, *, trusted=True, hostname="localhost", alpn=ALPN, noise=False, relay=False):
    process = await asyncio.create_subprocess_exec(
        args.dotnet, args.assembly, "serve", args.openssl,
        str(args.output / "certificate.pem"), str(args.output / "key.pem"),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    peers = []
    def create_peer(*positional, **keywords):
        peer = Peer(*positional, **keywords)
        peers.append(peer)
        return peer
    relay_transport = None
    relay_protocol = None
    success = False
    failure = None
    ready_line = b""
    stdout = b""
    stderr = b""
    try:
        ready_line = await asyncio.wait_for(process.stdout.readline(), 5)
        ready = json.loads(ready_line)
        if noise:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                for packet in (b"", b"bad", bytes(1200), b"\xc0" + bytes(1199)):
                    sender.sendto(packet, ("127.0.0.1", ready["port"]))
        port = ready["port"]
        if relay:
            relay_transport, relay_protocol = await asyncio.get_running_loop().create_datagram_endpoint(
                lambda: DatagramRelay(("127.0.0.1", ready["port"])), local_addr=("127.0.0.1", 0))
            port = relay_transport.get_extra_info("sockname")[1]
        configuration = QuicConfiguration(
            is_client=True, alpn_protocols=[alpn], server_name=hostname,
            verify_mode=ssl.CERT_REQUIRED, idle_timeout=3)
        if trusted:
            configuration.load_verify_locations(cafile=str(args.output / "certificate.pem"))
        async with asyncio.timeout(6):
            async with connect("127.0.0.1", port, configuration=configuration, create_protocol=create_peer) as peer:
                if peer.negotiated_alpn != ALPN:
                    raise AssertionError("ALPN mismatch")
                reader, writer = await peer.create_stream()
                for offset in range(0, len(payload), 113):
                    writer.write(payload[offset:offset + 113])
                    await writer.drain()
                writer.write_eof()
                received = await reader.read()
                if received != payload:
                    raise AssertionError(f"Echo mismatch: {len(received)} != {len(payload)}")
                success = True
    except (ConnectionError, TimeoutError, AssertionError, ValueError) as error:
        failure = type(error).__name__ + ": " + str(error)
    finally:
        try:
            stdout, stderr = await asyncio.wait_for(process.communicate(), 12)
        except TimeoutError:
            process.kill()
            stdout, stderr = await process.communicate()
            failure = "Server process did not terminate within the deadline"
        if relay_transport is not None:
            relay_transport.close()
        (args.output / (name + ".stdout.log")).write_bytes(ready_line + stdout)
        (args.output / (name + ".stderr.log")).write_bytes(stderr)
    positive = trusted and hostname == "localhost" and alpn == ALPN
    reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
    report = reports[-1] if reports else {}
    termination = peers[0].termination if peers else None
    expected_code = 0x178 if alpn != ALPN else 0x12a
    passed = (success and process.returncode == 0 and report.get("complete") is True) if positive else (
        not success and failure is not None and process.returncode == 1
        and not report.get("complete", False) and termination is not None
        and termination["code"] == expected_code)
    if noise and positive:
        passed = passed and report.get("rejectedPackets", 0) >= 4
    if relay:
        passed = passed and relay_protocol.injected and report.get("rejectedPackets", 0) >= 2
    return {"name": name, "passed": passed, "clientSucceeded": success,
            "clientFailure": failure, "termination": termination, "serverExit": process.returncode, "server": report}


async def main(args):
    lock = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    peer_pins = [line.split("==", 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith("aioquic==")]
    if len(peer_pins) != 1 or importlib.metadata.version("aioquic") != peer_pins[0]:
        raise RuntimeError("Independent peer does not match the repository dependency lock")
    args.output.mkdir(parents=True, exist_ok=False)
    subprocess.run([args.dotnet, args.assembly, "certificate", str(args.output)], check=True, capture_output=True)
    results = []
    for length in (0, 1, 128, 4096, 8192):
        results.append(await case(args, f"echo-{length}", bytes(i % 251 for i in range(length))))
        print(json.dumps(results[-1]), flush=True)
        if not results[-1]["passed"]:
            break
    if all(result["passed"] for result in results):
        for name, options in (
            ("untrusted-certificate", {"trusted": False}),
            ("wrong-hostname", {"hostname": "invalid.example"}),
            ("wrong-alpn", {"alpn": "different-protocol"}),
            ("malformed-before-valid", {"noise": True}),
            ("tamper-and-duplicate-established", {"relay": True}),
        ):
            results.append(await case(args, name, b"certificate-validated QUIC echo", **options))
            print(json.dumps(results[-1]), flush=True)
    libraries = {}
    for name in ("libssl-3-x64.dll", "libcrypto-3-x64.dll", "libssl.so.3", "libcrypto.so.3"):
        path = Path(args.openssl) / name
        if path.exists():
            libraries[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    receipt = {"peer": "aioquic", "peerVersion": importlib.metadata.version("aioquic"),
               "libraries": libraries, "assemblySha256": hashlib.sha256(Path(args.assembly).read_bytes()).hexdigest(),
               "sourceHashes": {str(path.name): hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(Path(__file__).parent.glob("*.cs"))},
               "cases": results,
               "passed": len(results) == 10 and all(result["passed"] for result in results)}
    (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--openssl", required=True)
    parser.add_argument("--output", required=True, type=Path)
    options = parser.parse_args()
    raise SystemExit(asyncio.run(main(options)))
