"""Independent stateless-reset eligibility, request abort and sibling checks."""
import argparse
import asyncio
import importlib.metadata
import json
import os
from pathlib import Path
import socket
from aioquic.asyncio import connect
from aioquic.quic.logger import QuicLogger
from aioquic.tls import CipherSuite
from verify import provenance
from verify_peer_connection_ids import IdPeer
from verify_tls_listener import CertificatePeer, configuration


def reset_packet(token, mode, server_id=None):
    packet = bytearray(os.urandom(21 if mode == "minimum" else 64))
    packet[0] = 0x40
    if mode == "known-cid":
        packet[1:1 + len(server_id)] = server_id
    elif mode == "clear-fixed":
        packet[0] = 0
    elif mode == "long-initial":
        packet[0:6] = b"\xc0\0\0\0\1\x08"
        packet[14] = 8
    packet[-16:] = token
    return bytes(packet)


async def case(args, cipher, mode):
    output = args.output / (cipher + "-" + mode)
    output.mkdir()
    host = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, "serve-webserver", args.profile,
        str(output / "certificate.pem"), str(output / "unused-key.pem"), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    startup, ready, failure, report = bytearray(), {}, None, {}
    trace, negatives, observed = QuicLogger(), 0, False
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
        async with asyncio.timeout(24):
            async with connect("127.0.0.1", ready["port"], configuration=config(), create_protocol=CertificatePeer) as sibling:
                sibling.authority = f"127.0.0.1:{ready['port']}".encode()
                async def request(peer, path):
                    _, fields, body = await peer.request(path)
                    assert fields[b":status"] == b"200", fields
                    return body
                async def state(wanted):
                    async with asyncio.timeout(3):
                        while await request(sibling, "/state/91") != wanted:
                            await asyncio.sleep(.01)
                async with connect("127.0.0.1", ready["port"], configuration=config(), create_protocol=IdPeer) as peer:
                    peer.authority = sibling.authority
                    assert peer._quic.tls.key_schedule.cipher_suite == CipherSuite[cipher]
                    assert sibling._quic.tls.key_schedule.cipher_suite == CipherSuite[cipher]
                    assert await request(peer, "/health") == b"controller HTTP/3"
                    _, pending = peer.start_request("/blocked/91")
                    await state(b"started")
                    def send(packet):
                        peer._transport.sendto(packet, peer._quic._network_paths[0].addr)
                    async def stays_alive():
                        await peer.ping()
                        assert await request(peer, "/health") == b"controller HTTP/3"
                        assert await request(sibling, "/state/91") == b"started"
                    spare = next(cid for cid in peer._quic._host_cids if cid.sequence_number > 0)
                    send(reset_packet(spare.stateless_reset_token, "unknown"))
                    await stays_alive()
                    negatives += 1
                    async def rotate():
                        candidate = next(cid for cid in peer._quic._host_cids if cid.sequence_number > peer.watermark)
                        peer.watermark = candidate.sequence_number
                        candidate.was_sent = False
                        peer.transmit()
                        peer._quic._host_cids = [cid for cid in peer._quic._host_cids if cid.sequence_number >= peer.watermark]
                        peer._quic.host_cid = candidate.cid
                        await stays_alive()
                        return candidate
                    first = await rotate()
                    current = await rotate()
                    send(reset_packet(first.stateless_reset_token, "unknown"))
                    await stays_alive()
                    negatives += 1
                    corrupt = bytearray(current.stateless_reset_token)
                    corrupt[-1] ^= 1
                    send(reset_packet(corrupt, "unknown"))
                    await stays_alive()
                    negatives += 1
                    short = bytearray(os.urandom(20))
                    short[-16:] = current.stateless_reset_token
                    send(bytes(short))
                    await stays_alive()
                    negatives += 1
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as foreign:
                        foreign.bind(("127.0.0.1", 0))
                        foreign.sendto(reset_packet(current.stateless_reset_token, "unknown"), ("127.0.0.1", ready["port"]))
                    await stays_alive()
                    negatives += 1
                    send(reset_packet(current.stateless_reset_token, mode, peer._quic._peer_cid.cid))
                    await state(b"aborted")
                    observed = True
                    pending.cancel()
                    assert await request(sibling, "/health") == b"controller HTTP/3"
                _, pending = sibling.start_request("/drain")
                host.stdin.write(b"drain\n")
                await host.stdin.drain()
                fields, body = await asyncio.wait_for(pending, 8)
                assert dict(fields)[b":status"] == b"200" and body == b"d" * 131072
        stdout, stderr = await asyncio.wait_for(host.communicate(), 10)
        reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
        report = reports[-1] if reports else {}
        assert host.returncode == 0 and report.get("completed") and report.get("drainCompleted") and report.get("rebind"), report
        assert report.get("aborted") == 1 and "active=0;" in report.get("transportDiagnostics", ""), report
        assert "reset-routes=0;peer-groups=0;" in report.get("transportDiagnostics", ""), report
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        if host.returncode is None:
            host.kill()
        stdout, stderr = await host.communicate()
    (output / "host.stdout.log").write_bytes(startup + stdout)
    (output / "host.stderr.log").write_bytes(stderr)
    (output / "peer.qlog").write_text(json.dumps(trace.to_dict()), encoding="utf-8")
    result = {"passed": failure is None, "failure": failure, "cipher": cipher, "mode": mode,
              "negativeCases": negatives, "resetObserved": observed, "exit": host.returncode, "server": report}
    (output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("passed", "failure", "cipher", "mode", "negativeCases", "resetObserved")}), flush=True)
    return result


async def main(args):
    lock = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    expected = [line.split("==", 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith("aioquic==")]
    assert len(expected) == 1 and importlib.metadata.version("aioquic") == expected[0]
    args.output.mkdir(parents=True, exist_ok=False)
    ciphers = ("AES_128_GCM_SHA256", "AES_256_GCM_SHA384") + (() if os.name == "nt" else ("CHACHA20_POLY1305_SHA256",))
    modes = ("unknown", "known-cid", "clear-fixed", "long-initial", "minimum")
    results = []
    for cipher in ciphers:
        for mode in modes:
            results.append(await case(args, cipher, mode))
            if not results[-1]["passed"]:
                break
        if not results[-1]["passed"]:
            break
    result = {**provenance(args), "passed": len(results) == len(ciphers) * len(modes) and all(x["passed"] for x in results),
              "cases": results, "scope": "Received reset token eligibility and endpoint binding, real controller cancellation, healthy sibling, drain/rebind. Server reset emission, path migration and local CID issuance are separate acceptance work."}
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
