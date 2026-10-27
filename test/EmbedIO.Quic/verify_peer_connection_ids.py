"""Independent encrypted peer-ID retirement, limits and healthy-sibling checks."""
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


class IdPeer(CertificatePeer):
    def __init__(self, *values, **keywords):
        super().__init__(*values, **keywords)
        self.watermark = 0
        self.invalid = None
        self.sent_ids = []
        self._quic._write_new_connection_id_frame = self.write_id

    def write_id(self, builder, connection_id):
        sequence = connection_id.sequence_number
        prior = min(self.watermark, sequence)
        identity, token = connection_id.cid, connection_id.stateless_reset_token
        if self.invalid == "bad-prior":
            prior = sequence + 1
        elif self.invalid == "zero-length":
            identity = b""
        elif self.invalid == "conflict":
            identity = os.urandom(len(identity))
        elif self.invalid == "too-many":
            sequence = self._quic._host_cid_seq + 1
            identity, token = os.urandom(len(identity)), os.urandom(16)
        # Original test encoder for RFC9000 field order. The pinned peer supplies
        # TLS, packet protection and ACK/loss delivery; no transport code is copied.
        frame = builder.start_frame(QuicFrameType.NEW_CONNECTION_ID, capacity=64,
            handler=self._quic._on_new_connection_id_delivery, handler_args=(connection_id,))
        frame.push_uint_var(sequence)
        frame.push_uint_var(prior)
        frame.push_uint8(len(identity))
        frame.push_bytes(identity)
        frame.push_bytes(token)
        connection_id.was_sent = True
        self.sent_ids.append({"sequence": sequence, "retirePrior": prior, "id": identity.hex()})


async def case(args, cipher, length, invalid=None):
    name = invalid or cipher + "-" + str(length)
    output = args.output / name
    output.mkdir()
    process = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, "serve-webserver", args.openssl,
        str(output / "certificate.pem"), str(output / "unused-key.pem"), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    ready, startup, failure, report = {}, bytearray(), None, {}
    rotations, healthy, retired = 0, 0, set()
    trace = None
    sent_ids = []
    try:
        async with asyncio.timeout(8):
            while not ready.get("ready"):
                line = await process.stdout.readline()
                startup.extend(line)
                if not line or len(startup) > 32768:
                    raise RuntimeError("Host ended or exceeded its readiness output limit")
                if line.startswith(b"{"):
                    ready = json.loads(line)
        def config(size=8):
            value = configuration(output / "certificate.pem", "localhost")
            value.cipher_suites = [CipherSuite[cipher]]
            value.connection_id_length = size
            return value
        peer_config = config(length)
        peer_config.quic_logger = QuicLogger()
        trace = peer_config.quic_logger
        async with asyncio.timeout(24):
            async with connect("127.0.0.1", ready["port"], configuration=config(), create_protocol=CertificatePeer) as sibling:
                async with connect("127.0.0.1", ready["port"], configuration=peer_config, create_protocol=IdPeer) as peer:
                    for connection in (peer, sibling):
                        connection.authority = f"127.0.0.1:{ready['port']}".encode()
                        assert connection._quic.tls.key_schedule.cipher_suite == CipherSuite[cipher]
                    async def health(connection):
                        _, fields, body = await connection.request("/health")
                        assert fields[b":status"] == b"200" and body == b"controller HTTP/3"
                    await health(peer)
                    await peer.ping()
                    if invalid:
                        candidate = next(cid for cid in peer._quic._host_cids if cid.sequence_number > 0)
                        peer.invalid = invalid
                        candidate.was_sent = False
                        peer.transmit()
                        async with asyncio.timeout(5):
                            while peer.termination is None:
                                await asyncio.sleep(0.01)
                        assert peer.termination.error_code == {"bad-prior": 7, "zero-length": 7, "conflict": 10, "too-many": 9}[invalid], peer.termination
                    else:
                        for _ in range(16):
                            candidate = next(cid for cid in peer._quic._host_cids if cid.sequence_number > peer.watermark)
                            peer.watermark = candidate.sequence_number
                            candidate.was_sent = False
                            peer.transmit()
                            peer._quic._host_cids = [cid for cid in peer._quic._host_cids if cid.sequence_number >= peer.watermark]
                            peer._quic.host_cid = candidate.cid
                            await health(peer)
                            await peer.ping()
                            await health(sibling)
                            healthy += 1
                            rotations += 1
                    sent_ids = peer.sent_ids
                await health(sibling)
                healthy += 1
                _, pending = sibling.start_request("/drain")
                process.stdin.write(b"drain\n")
                await process.stdin.drain()
                fields, body = await asyncio.wait_for(pending, 8)
                assert dict(fields)[b":status"] == b"200" and body == b"d" * 131072
        stdout, stderr = await asyncio.wait_for(process.communicate(), 10)
        reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
        report = reports[-1] if reports else {}
        assert process.returncode == 0 and report.get("completed") is True and report.get("drainCompleted") is True and report.get("rebind") is True, report
        assert report.get("healthRequests") == (2 if invalid else 34), report
        for entry in trace.to_dict()["traces"]:
            for event in entry["events"]:
                if event["name"] == "transport:packet_received":
                    for frame in event["data"].get("frames", []):
                        if frame.get("frame_type") == "retire_connection_id":
                            retired.add(frame["sequence_number"])
        if not invalid:
            assert set(range(rotations)) <= retired, retired
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        if process.returncode is None:
            process.kill()
        stdout, stderr = await process.communicate()
    (output / "host.stdout.log").write_bytes(startup + stdout)
    (output / "host.stderr.log").write_bytes(stderr)
    if trace is not None:
        (output / "peer.qlog").write_text(json.dumps(trace.to_dict()), encoding="utf-8")
    result = {"name": name, "passed": failure is None, "failure": failure, "cipher": cipher, "cidLength": length,
              "rotations": rotations, "healthySiblingRequests": healthy, "retired": sorted(retired),
              "encodedIds": sent_ids, "exit": process.returncode, "server": report}
    (output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result), flush=True)
    return result


async def main(args):
    pins = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    expected = [line.split("==", 1)[1].split()[0] for line in pins.read_text().splitlines() if line.startswith("aioquic==")]
    assert len(expected) == 1 and importlib.metadata.version("aioquic") == expected[0]
    args.output.mkdir(parents=True, exist_ok=False)
    ciphers = ("AES_128_GCM_SHA256", "AES_256_GCM_SHA384") + (() if os.name == "nt" else ("CHACHA20_POLY1305_SHA256",))
    results = []
    for cipher, length in [(cipher, length) for cipher in ciphers for length in (8, 20)]:
        results.append(await case(args, cipher, length))
        if not results[-1]["passed"]:
            break
    if all(result["passed"] for result in results):
        for invalid in ("bad-prior", "zero-length", "conflict", "too-many"):
            results.append(await case(args, ciphers[0], 8, invalid))
            if not results[-1]["passed"]:
                break
    result = {**provenance(args), "passed": len(results) == len(ciphers) * 2 + 4 and all(r["passed"] for r in results),
              "cases": results, "scope": "Actual WebServer HTTP/3 peer CID retirement/limits, verified TLS/ciphers, healthy sibling, drain/rebind. Does not validate local-issued CID rotation, path migration, Retry or stateless reset."}
    (args.output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.openssl = "schannel-rsa" if os.name == "nt" else "portable-rsa"
    raise SystemExit(asyncio.run(main(args)))
