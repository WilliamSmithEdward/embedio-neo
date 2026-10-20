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
import xml.etree.ElementTree as ET

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
    def __init__(self, server, mode="tamper"):
        self.server = server
        self.mode = mode
        self.server_initial = 0
        self.server_short = 0
        self.client_short = 0
        self.dropped = 0
        self.reordered = 0
        self.client = None
        self.transport = None
        self.injected = False

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, address):
        if address == self.server:
            long_header = bool(data and data[0] & 0x80)
            packet_type = (data[0] >> 4) & 3 if long_header else -1
            if packet_type == 0:
                self.server_initial += 1
            if not long_header:
                self.server_short += 1
            # Select response-sized encrypted packets; ordinal counts can select only ACKs.
            drop = (self.mode == "loss-initial" and self.server_initial == 2 and packet_type == 0
                    or self.mode == "loss-handshake" and packet_type == 2
                    or self.mode == "loss-stream" and len(data) > 512 and not long_header)
            if drop and self.dropped < (3 if self.mode == "loss-stream" else 1):
                self.dropped += 1
                return
            if self.client is not None:
                if self.mode == "reorder" and not long_header and self.reordered == 0:
                    self.reordered += 1
                    asyncio.get_running_loop().call_later(0.08, self.transport.sendto, data, self.client)
                else:
                    self.transport.sendto(data, self.client)
            return
        if self.client is None:
            self.client = address
        if address != self.client:
            return
        if data and not data[0] & 0x80:
            self.client_short += 1
            if self.mode == "loss-ack" and self.client_short == 2:
                self.dropped += 1
                return
        if self.mode == "tamper" and data and not data[0] & 0x80 and not self.injected:
            self.injected = True
            self.transport.sendto(data[:5], self.server)
            altered = bytearray(data)
            altered[-1] ^= 1
            self.transport.sendto(altered, self.server)
            self.transport.sendto(data, self.server)
        self.transport.sendto(data, self.server)


async def case(args, name, payload, *, trusted=True, hostname="localhost", alpn=ALPN, noise=False, relay=False, multiplexed=False, rounds=1, parallel=1, unidirectional=False, deadline=6, async_streams=False, close_code=None):
    process = await asyncio.create_subprocess_exec(
        args.dotnet, args.assembly, "serve-async-close" if close_code is not None else "serve-async" if async_streams else "serve-streams" if multiplexed else "serve", args.openssl,
        str(args.output / "certificate.pem"), str(args.output / "key.pem"),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    peers = []
    inbound = asyncio.Queue(maxsize=16)
    def stream_handler(reader, writer):
        # The async host alternates server uni/bidi responses for incoming uni
        # requests. Finish the unused client half of a server-initiated bidi.
        if writer.can_write_eof():
            writer.write_eof()
        inbound.put_nowait(reader)
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
                lambda: DatagramRelay(("127.0.0.1", ready["port"]), "tamper" if relay is True else relay), local_addr=("127.0.0.1", 0))
            port = relay_transport.get_extra_info("sockname")[1]
        configuration = QuicConfiguration(
            is_client=True, alpn_protocols=[alpn], server_name=hostname,
            verify_mode=ssl.CERT_REQUIRED, idle_timeout=3)
        if trusted:
            configuration.load_verify_locations(cafile=str(args.output / "certificate.pem"))
        async with asyncio.timeout(deadline):
            async with connect("127.0.0.1", port, configuration=configuration, create_protocol=create_peer, stream_handler=stream_handler) as peer:
                if peer.negotiated_alpn != ALPN:
                    raise AssertionError("ALPN mismatch")
                async def exchange(index):
                    expected = index.to_bytes(2, "big") + payload if parallel > 1 else payload
                    reader, writer = await peer.create_stream(is_unidirectional=unidirectional)
                    # aioquic reserves the ID on the first write. Reserve it before
                    # creating upload tasks so concurrent public create_stream calls
                    # cannot return the same still-unused ID.
                    writer.write(b"")
                    async def upload():
                        chunk = 16384 if len(expected) > 8192 else 113
                        for offset in range(0, len(expected), chunk):
                            writer.write(expected[offset:offset + chunk])
                            await writer.drain()
                        writer.write_eof()
                    async def download():
                        response = await inbound.get() if unidirectional else reader
                        received = await response.read()
                        if received != expected:
                            raise AssertionError(f"Stream {index} echo mismatch: {len(received)} != {len(expected)}")
                    await asyncio.gather(upload(), download())
                for iteration in range(rounds):
                    await asyncio.gather(*(exchange(iteration * parallel + index) for index in range(parallel)))
                if close_code is not None:
                    await peer.wait_closed()
                    if peer.termination is None or peer.termination["code"] != close_code:
                        raise AssertionError("Wrong application close code")
                elif multiplexed:
                    # Let the independent peer's normal delayed-ACK timer run, then
                    # complete a public ping before checking ACK-gated retirement.
                    await asyncio.sleep(0.1)
                    await peer.ping()
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
    passed = passed and report.get("phase") == "Closed"
    if multiplexed:
        passed = passed and report.get("rawStreamsCompleted") == rounds * parallel
        passed = passed and report.get("retiredStreams") == rounds * parallel * (2 if unidirectional else 1)
    if close_code is not None:
        passed = passed and termination is not None and termination["code"] == close_code
        passed = passed and report.get("closePackets", 0) > 0
    if noise and positive:
        passed = passed and report.get("rejectedPackets", 0) >= 4
    if relay is True:
        passed = passed and relay_protocol.injected and report.get("rejectedPackets", 0) >= 2
    elif relay:
        passed = passed and relay_protocol.dropped + relay_protocol.reordered > 0
        if relay in ("loss-initial", "loss-handshake", "loss-stream"):
            passed = passed and report.get("retransmittedPackets", 0) > 0
        if relay == "loss-stream":
            passed = passed and relay_protocol.dropped == 3
    return {"name": name, "passed": passed, "clientSucceeded": success,
            "clientFailure": failure, "termination": termination, "serverExit": process.returncode, "server": report}


def provenance(args):
    project = Path(__file__).resolve().parent
    root = project.parents[1]
    files = set(project.glob("*.cs")) | set(project.glob("*.py")) | set(project.glob("*.csproj"))
    files.update(project.glob("peer/*"))
    engine = root / "src/EmbedIO.Quic"
    files.update(path for path in engine.rglob("*")
                 if path.is_file() and path.suffix in (".cs", ".csproj", ".json")
                 and not {"bin", "obj"}.intersection(path.relative_to(engine).parts))
    files = {path for path in files if path.is_file()}
    for item in ET.parse(project / "EmbedIO.Quic.csproj").iter("Compile"):
        included = project / item.attrib["Include"]
        files.update(included.parent.glob(included.name))
    for name in ("global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
                 ".editorconfig", "test/EmbedIO.Conformance/drivers/requirements.txt"):
        path = root / name
        if path.exists():
            files.add(path)
    libraries = {}
    for name in ("libssl-3-x64.dll", "libcrypto-3-x64.dll", "libssl.so.3", "libcrypto.so.3"):
        path = Path(args.openssl) / name
        if path.exists():
            libraries[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    return {"libraries": libraries,
            "assemblySha256": hashlib.sha256(Path(args.assembly).read_bytes()).hexdigest(),
            "engineAssemblySha256": hashlib.sha256((Path(args.assembly).parent / "EmbedIO.Quic.dll").read_bytes()).hexdigest(),
            "sdk": subprocess.run([args.dotnet, "--version"], check=True, capture_output=True, text=True).stdout.strip(),
            "sourceHashes": {path.resolve().relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                             for path in sorted(files)}}


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
    if all(result["passed"] for result in results):
        for mode in ("loss-initial", "loss-handshake", "loss-stream", "loss-ack", "reorder"):
            results.append(await case(args, mode, bytes(i % 251 for i in range(8192)), relay=mode))
            print(json.dumps(results[-1]), flush=True)
    if all(result["passed"] for result in results):
        for name, length, options in (
            ("raw-bidi-256", 64, {"rounds": 256}),
            ("raw-uni-128", 64, {"rounds": 128, "unidirectional": True}),
            ("raw-parallel-16", 4096, {"parallel": 16, "rounds": 4}),
            ("raw-sliding-2m-loss", 2 * 1024 * 1024, {"relay": "loss-stream", "deadline": 20}),
        ):
            results.append(await case(args, name, bytes(i % 251 for i in range(length)), multiplexed=True, **options))
            print(json.dumps(results[-1]), flush=True)
            if not results[-1]["passed"]:
                break
    if all(result["passed"] for result in results):
        for name, length, options in (
            ("async-bidi-128", 64, {"rounds": 128}),
            ("async-uni-128", 64, {"rounds": 128, "unidirectional": True}),
            ("async-parallel-16", 4096, {"parallel": 16, "rounds": 4}),
            ("async-sliding-2m-loss", 2 * 1024 * 1024, {"relay": "loss-stream", "deadline": 20}),
            ("async-application-close", 1024, {"close_code": 0x1234}),
        ):
            results.append(await case(args, name, bytes(i % 251 for i in range(length)), multiplexed=True, async_streams=True, **options))
            print(json.dumps(results[-1]), flush=True)
            if not results[-1]["passed"]:
                break
    receipt = {"peer": "aioquic", "peerVersion": importlib.metadata.version("aioquic"),
               **provenance(args),
               "cases": results,
               "passed": len(results) == 24 and all(result["passed"] for result in results)}
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
