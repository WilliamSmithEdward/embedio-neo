"""Independent server-ID rotation, retired-ID reset emission and sibling checks."""
import argparse
import asyncio
import importlib.metadata
import json
import os
from pathlib import Path
from aioquic.asyncio import connect
from aioquic.quic.logger import QuicLogger
from aioquic.tls import CipherSuite
from verify import provenance
from verify_tls_listener import CertificatePeer, configuration


class LocalIdPeer(CertificatePeer):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.expected_reset = None
        self.reset_seen = asyncio.Event()
        self.reset_lengths = []

    def datagram_received(self, data, addr):
        if self.expected_reset is not None and len(data) >= 21 and data[-16:] == self.expected_reset:
            assert len(self.reset_lengths) < 16, "Reset response exceeded the test's observation bound"
            self.reset_lengths.append(len(data))
            self.reset_seen.set()
        super().datagram_received(data, addr)


async def case(args, cipher):
    output = args.output / cipher
    output.mkdir()
    host = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, "serve-webserver", args.profile,
        str(output / "certificate.pem"), str(output / "unused-key.pem"), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    startup, failure, report, ready = bytearray(), None, {}, {}
    trace, rotations, resets, healthy = QuicLogger(), 0, 0, 0
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
            value.cipher_suites = [CipherSuite[cipher]]
            value.quic_logger = trace
            return value
        async with asyncio.timeout(30):
            async with connect("127.0.0.1", ready["port"], configuration=config(), create_protocol=CertificatePeer) as sibling:
                async with connect("127.0.0.1", ready["port"], configuration=config(), create_protocol=LocalIdPeer) as peer:
                    for connection in (peer, sibling):
                        connection.authority = f"127.0.0.1:{ready['port']}".encode()
                    async def health(connection):
                        nonlocal healthy
                        _, fields, body = await connection.request("/health")
                        assert fields[b":status"] == b"200" and body == b"controller HTTP/3"
                        healthy += 1
                    async def spare():
                        async with asyncio.timeout(4):
                            while not peer._quic._peer_cid_available:
                                await peer.ping()
                                await asyncio.sleep(0.002)
                    await health(peer)
                    await health(sibling)
                    initial = peer._quic._peer_cid
                    assert len(initial.cid) == 16 and len(initial.stateless_reset_token or b"") == 16, "Missing initial server reset token or 128-bit ID"
                    await spare()
                    original, original_token = initial.cid, initial.stateless_reset_token
                    prior = initial.sequence_number
                    seen = {original}
                    for _ in range(32):
                        await spare()
                        peer.change_connection_id()
                        await peer.ping()
                        current = peer._quic._peer_cid
                        assert current.sequence_number > prior and len(current.cid) == 16 and current.cid not in seen
                        assert len(current.stateless_reset_token or b"") == 16
                        prior = current.sequence_number
                        seen.add(current.cid)
                        rotations += 1
                        await health(peer)
                        await health(sibling)
                    # The retired initial alias no longer reaches active application state.
                    # Its independently learned token still identifies the emitted reset.
                    peer.expected_reset = original_token
                    for length in (37, 64):
                        peer.reset_seen.clear()
                        packet = bytearray(os.urandom(length))
                        packet[0] = 0x40
                        packet[1:17] = original
                        peer._transport.sendto(bytes(packet), peer._quic._network_paths[0].addr)
                        await asyncio.wait_for(peer.reset_seen.wait(), 4)
                        assert peer.reset_lengths[-1] == min(43, length - 1)
                        resets += 1
                        await health(peer)
                        await health(sibling)
                    peer.expected_reset = peer._quic._peer_cid.stateless_reset_token
                    before = len(peer.reset_lengths)
                    packet = bytearray(os.urandom(64))
                    packet[0] = 0x40
                    packet[1:17] = peer._quic._peer_cid.cid
                    peer._transport.sendto(bytes(packet), peer._quic._network_paths[0].addr)
                    await peer.ping()
                    await health(peer)
                    assert len(peer.reset_lengths) == before, "Active CID elicited a stateless reset"
                _, pending = sibling.start_request("/drain")
                host.stdin.write(b"drain\n")
                await host.stdin.drain()
                fields, body = await asyncio.wait_for(pending, 8)
                assert dict(fields)[b":status"] == b"200" and body == b"d" * 131072
        stdout, stderr = await asyncio.wait_for(host.communicate(), 10)
        reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
        report = reports[-1] if reports else {}
        assert host.returncode == 0 and report.get("completed") and report.get("drainCompleted") and report.get("rebind"), report
        assert "active=0;" in report.get("transportDiagnostics", "") and "issued-routes=0;" in report.get("transportDiagnostics", ""), report
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        if host.returncode is None:
            host.kill()
        stdout, stderr = await host.communicate()
    (output / "host.stdout.log").write_bytes(startup + stdout)
    (output / "host.stderr.log").write_bytes(stderr)
    (output / "peer.qlog").write_text(json.dumps(trace.to_dict()), encoding="utf-8")
    result = {"passed": failure is None, "failure": failure, "cipher": cipher, "rotations": rotations,
              "resetEmissions": resets, "healthyRequests": healthy, "exit": host.returncode, "server": report}
    (output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("passed", "failure", "cipher", "rotations", "resetEmissions", "healthyRequests")}), flush=True)
    return result


async def main(args):
    lock = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    expected = [line.split("==", 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith("aioquic==")]
    assert len(expected) == 1 and importlib.metadata.version("aioquic") == expected[0]
    args.output.mkdir(parents=True, exist_ok=False)
    ciphers = ("AES_128_GCM_SHA256", "AES_256_GCM_SHA384") + (() if os.name == "nt" else ("CHACHA20_POLY1305_SHA256",))
    results = []
    for cipher in ciphers:
        results.append(await case(args, cipher))
        if not results[-1]["passed"]:
            break
    result = {**provenance(args), "passed": len(results) == len(ciphers) and all(x["passed"] for x in results),
              "cases": results, "scope": "Server-issued ID retirement, reset token/layout/size from retired aliases, healthy siblings and complete cleanup/drain/rebind. Path migration and restart-persisted listener secrets remain separate."}
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
