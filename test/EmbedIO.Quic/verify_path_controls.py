"""Pinned independent-peer path-response padding, one-shot loss and sibling checks."""
import argparse
import asyncio
import importlib.metadata
import json
import os
from pathlib import Path
from quic_client import connect
from aioquic.quic.logger import QuicLogger
from aioquic.quic.packet import QuicFrameType
from aioquic.tls import CipherSuite
from verify import provenance
from verify_tls_listener import CertificatePeer, configuration


class PeerTransport:
    def __init__(self, transport, peer):
        self.transport, self.peer = transport, peer
    def sendto(self, data, address=None):
        if self.peer.withhold:
            self.peer.withheld += 1
        else:
            self.transport.sendto(data, address)
        if self.peer.challenge_sent:
            self.peer.challenge_sent = False
            self.peer.withhold = True
    def __getattr__(self, name):
        return getattr(self.transport, name)


class PathPeer(CertificatePeer):
    def __init__(self, *values, **keywords):
        super().__init__(*values, **keywords)
        self.withhold, self.withheld = False, 0
        self.challenge = None
        self.challenge_sent = False
        self.datagram_length = 0
        self.path_responses = []
        self.seen = set()
        self.observed = asyncio.Event()
        # These hooks are specific to the locked aioquic version. Its encoder,
        # cryptography, recovery and first-response validator remain in use.
        handlers = getattr(self._quic, "_QuicConnection__frame_handlers")
        original, epochs = handlers[0x1b]
        def observe(context, frame_type, buffer):
            data = buffer.data_slice(buffer.tell(), buffer.tell() + 8)
            assert len(self.path_responses) < 16, "Path-response observation bound exceeded"
            self.path_responses.append({"data": data.hex(), "datagramBytes": self.datagram_length})
            if data in self.seen:
                # Retain a duplicate as failing evidence rather than letting the
                # peer close before the one-response assertion can report it.
                buffer.pull_bytes(8)
            else:
                original(context, frame_type, buffer)
                self.seen.add(data)
            self.observed.set()
        handlers[0x1b] = (observe, epochs)
        original_ping = self._quic._write_ping_frame
        def write_probe(builder, uids=(), comment=""):
            original_ping(builder, uids, comment)
            if self.challenge is not None:
                challenge, self.challenge = self.challenge, None
                frame = builder.start_frame(QuicFrameType.PATH_CHALLENGE, capacity=9)
                frame.push_bytes(challenge)
                path = self._quic._network_paths[0]
                path.is_validated, path.local_challenge_sent = False, True
                self._quic._add_local_challenge(challenge, path)
                self.challenge_sent = True
        self._quic._write_ping_frame = write_probe
    def connection_made(self, transport):
        super().connection_made(PeerTransport(transport, self))
    def datagram_received(self, data, address):
        self.datagram_length = len(data)
        super().datagram_received(data, address)


async def case(args, cipher):
    output = args.output / cipher
    output.mkdir()
    host = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, "serve-webserver", args.profile,
        str(output / "certificate.pem"), str(output / "unused-key.pem"), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    startup, stdout, stderr = bytearray(), b"", b""
    ready, report, failure, observations, withheld, healthy = {}, {}, None, [], 0, 0
    trace = QuicLogger()
    try:
        async with asyncio.timeout(8):
            while not ready.get("ready"):
                line = await host.stdout.readline()
                startup.extend(line)
                if not line or len(startup) > 32768:
                    raise RuntimeError("Host readiness ended or exceeded its limit")
                if line.startswith(b"{"):
                    ready = json.loads(line)
        def config():
            value = configuration(output / "certificate.pem", "localhost")
            value.cipher_suites, value.quic_logger, value.idle_timeout = [CipherSuite[cipher]], trace, 10
            return value
        async with asyncio.timeout(24):
            async with connect("127.0.0.1", ready["port"], configuration=config(), create_protocol=CertificatePeer) as sibling:
                async with connect("127.0.0.1", ready["port"], configuration=config(), create_protocol=PathPeer) as peer:
                    for connection in (peer, sibling):
                        connection.authority = f"127.0.0.1:{ready['port']}".encode()
                    async def health(connection):
                        nonlocal healthy
                        _, fields, body = await connection.request("/health")
                        assert fields[b":status"] == b"200" and body == b"controller HTTP/3"
                        healthy += 1
                    await health(peer)
                    await health(sibling)
                    await peer.ping()
                    await asyncio.sleep(0.05)
                    path = peer._quic._network_paths[0]
                    peer.challenge = os.urandom(8)
                    peer._quic.send_ping(0x50415448)
                    peer.transmit()
                    try:
                        await asyncio.wait_for(peer.observed.wait(), 4)
                        assert path.is_validated, "Independent peer did not validate echoed challenge"
                        await health(sibling)
                        # Suppress the peer's datagrams for more than an initial
                        # PTO; ordinary server recovery remains enabled.
                        await asyncio.sleep(2)
                    finally:
                        peer.withhold = False
                        observations, withheld = list(peer.path_responses), peer.withheld
                    assert withheld > 0, "No acknowledgments were withheld"
                    assert len(observations) == 1, "A response was repeated without a fresh challenge"
                    assert observations[0]["datagramBytes"] >= 1200, "Response datagram was not expanded to 1200 bytes"
                    await peer.ping()
                    await health(peer)
                    await health(sibling)
                _, pending = sibling.start_request("/drain")
                host.stdin.write(b"drain\n")
                await host.stdin.drain()
                fields, body = await asyncio.wait_for(pending, 8)
                assert dict(fields)[b":status"] == b"200" and body == b"d" * 131072
        stdout, stderr = await asyncio.wait_for(host.communicate(), 10)
        reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
        report = reports[-1] if reports else {}
        assert host.returncode == 0 and report.get("completed") and report.get("drainCompleted") and report.get("rebind"), report
        assert "active=0;" in report.get("transportDiagnostics", ""), report
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        if host.returncode is None:
            host.kill()
        stdout, stderr = await host.communicate()
    (output / "host.stdout.log").write_bytes(startup + stdout)
    (output / "host.stderr.log").write_bytes(stderr)
    (output / "peer.qlog").write_text(json.dumps(trace.to_dict()), encoding="utf-8")
    result = {"passed": failure is None, "failure": failure, "cipher": cipher, "responses": observations,
              "withheldPeerDatagrams": withheld, "healthyRequests": healthy, "exit": host.returncode, "server": report}
    (output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("passed", "failure", "cipher", "responses", "withheldPeerDatagrams", "healthyRequests")}), flush=True)
    return result


async def main(args):
    lock = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    expected = [line.split("==", 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith("aioquic==")]
    assert len(expected) == 1 and importlib.metadata.version("aioquic") == expected[0]
    args.output.mkdir(parents=True, exist_ok=False)
    ciphers = ("AES_128_GCM_SHA256", "AES_256_GCM_SHA384") + (() if os.name == "nt" else ("CHACHA20_POLY1305_SHA256",))
    cases = []
    for cipher in ciphers:
        cases.append(await case(args, cipher))
        if not cases[-1]["passed"]:
            break
    result = {**provenance(args), "passed": len(cases) == len(ciphers) and all(case["passed"] for case in cases),
              "cases": cases, "scope": "Same-path encrypted response padding, one-response under withheld ACKs, siblings and complete drain/rebind. NAT rebinding and server path initiation are separate integration work."}
    (args.output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.profile = "schannel-rsa" if os.name == "nt" else "portable-rsa"
    args.openssl = args.profile
    raise SystemExit(asyncio.run(main(args)))
