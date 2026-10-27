"""Pinned HTTP/3 peer through controlled source-port, IP and CID changes."""
import argparse
import asyncio
import importlib.metadata
import json
import os
from pathlib import Path
from quic_client import connect
from aioquic.quic.logger import QuicLogger
from aioquic.tls import CipherSuite
from verify import provenance
from verify_tls_listener import CertificatePeer, configuration


class Backend(asyncio.DatagramProtocol):
    def __init__(self, relay):
        self.relay = relay
        self.transport = None
        self.sent = self.received = self.sent_bytes = self.received_bytes = 0
        self.errors = []
        self.blocked, self.dropped, self.maximum_received = False, 0, 0
        self.padded = asyncio.Event()

    def connection_made(self, transport):
        self.transport = transport

    def send(self, data):
        self.sent += 1
        self.sent_bytes += len(data)
        self.transport.sendto(data, self.relay.server)

    def datagram_received(self, data, address):
        if address != self.relay.server or self.relay.client is None:
            return
        self.received += 1
        self.received_bytes += len(data)
        self.maximum_received = max(self.maximum_received, len(data))
        if len(data) >= 1200:
            self.padded.set()
        if self.blocked:
            self.dropped += 1
        else:
            self.relay.transport.sendto(data, self.relay.client)

    def error_received(self, error):
        if len(self.errors) < 8:
            self.errors.append(repr(error))

    def receipt(self):
        return {"endpoint": self.transport.get_extra_info("sockname"), "sent": self.sent,
                "received": self.received, "sentBytes": self.sent_bytes,
                "receivedBytes": self.received_bytes, "maximumReceived": self.maximum_received, "dropped": self.dropped, "errors": self.errors}


class NatRelay(asyncio.DatagramProtocol):
    # A fixed client-facing address and a replaceable server-facing socket change
    # the UDP source port/IP independently of the client-facing QUIC connection.
    def __init__(self, server):
        self.server = server
        self.transport = self.client = self.current = None
        self.backends = []

    async def rebind(self, address="127.0.0.1"):
        assert len(self.backends) < 8, "Relay endpoint bound exceeded"
        _, backend = await asyncio.get_running_loop().create_datagram_endpoint(
            lambda: Backend(self), local_addr=(address, 0))
        self.backends.append(backend)
        self.current = backend
        return backend

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, address):
        if self.client is None:
            self.client = address
        if address == self.client and self.current is not None:
            self.current.send(data)

    def close(self):
        if self.transport is not None:
            self.transport.close()
        for backend in self.backends:
            backend.transport.close()


async def case(args, cipher, mode):
    output = args.output / (cipher + "-" + mode)
    output.mkdir()
    host = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, "serve-webserver", args.profile,
        str(output / "certificate.pem"), str(output / "unused-key.pem"), stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    ready, report, failure = {}, {}, None
    startup, stdout, stderr = bytearray(), b"", b""
    trace, relay, healthy, stage = QuicLogger(), None, 0, "host-readiness"
    backend_receipts, communicated, identity = [], False, b""
    try:
        async with asyncio.timeout(8):
            while not ready.get("ready"):
                line = await host.stdout.readline()
                startup.extend(line)
                if not line or len(startup) > 32768:
                    raise RuntimeError("Host readiness ended or exceeded its limit")
                if line.startswith(b"{"):
                    ready = json.loads(line)
        relay = NatRelay(("127.0.0.1", ready["port"]))
        await relay.rebind()
        await asyncio.get_running_loop().create_datagram_endpoint(
            lambda: relay, local_addr=("127.0.0.1", 0))
        relay_port = relay.transport.get_extra_info("sockname")[1]
        def config():
            value = configuration(output / "certificate.pem", "localhost")
            value.cipher_suites, value.quic_logger, value.idle_timeout = [CipherSuite[cipher]], trace, 10
            return value
        async with asyncio.timeout(45):
            stage = "independent-handshakes"
            async with connect("127.0.0.1", ready["port"], configuration=config(), create_protocol=CertificatePeer) as sibling:
                async with connect("127.0.0.1", relay_port, configuration=config(), create_protocol=CertificatePeer) as peer:
                    for connection in (peer, sibling):
                        connection.authority = f"127.0.0.1:{ready['port']}".encode()
                    async def health(connection):
                        nonlocal healthy
                        _, fields, body = await asyncio.wait_for(connection.request("/health"), 5)
                        assert fields[b":status"] == b"200" and body == b"controller HTTP/3"
                        healthy += 1
                    stage = "original-path"
                    await health(peer)
                    await health(sibling)
                    async def metadata():
                        _, fields, body = await peer.request("/metadata")
                        assert fields[b":status"] == b"200"
                        return body
                    identity = await metadata()
                    assert identity.endswith(f"|127.0.0.1:{relay.backends[0].transport.get_extra_info('sockname')[1]}".encode())
                    blocked, pending_abort = peer.start_request("/blocked/91")
                    async def state(wanted):
                        async with asyncio.timeout(3):
                            while True:
                                _, fields, body = await sibling.request("/state/91")
                                assert fields[b":status"] == b"200"
                                if body == wanted:
                                    return
                                await asyncio.sleep(0.01)
                    await state(b"started")
                    await peer.ping()
                    # Let any pending handshake ACK cross the old path before
                    # testing only post-confirmation address changes.
                    await asyncio.sleep(0.05)
                    async def move(index):
                        if mode == "cid-ip":
                            async with asyncio.timeout(3):
                                while not peer._quic._peer_cid_available:
                                    await peer.ping()
                                    await asyncio.sleep(0.01)
                        # Switch the source first: change_connection_id transmits
                        # immediately, so reversing these calls tests another case.
                        await relay.rebind("127.0.0.2" if mode == "cid-ip" and index == 2 else "127.0.0.1")
                        if mode == "cid-ip":
                            previous = peer._quic._peer_cid.sequence_number
                            peer.change_connection_id()
                            assert peer._quic._peer_cid.sequence_number > previous
                    original = relay.current.transport.get_extra_info("sockname")
                    await move(0)
                    assert relay.current.transport.get_extra_info("sockname") != original
                    stage = "rebound-ping"
                    await asyncio.wait_for(peer.ping(), 5)
                    stage = "rebound-controller"
                    await asyncio.wait_for(relay.current.padded.wait(), 5)
                    await health(peer)
                    await health(sibling)
                    # Multiple source-port changes must retain one application
                    # connection and not leave unbounded dispatcher aliases.
                    for index in range(1, 3):
                        await move(index)
                        stage = "repeated-rebound-ping"
                        await asyncio.wait_for(peer.ping(), 5)
                        stage = "repeated-rebound-controller"
                        await asyncio.wait_for(relay.current.padded.wait(), 5)
                        await health(peer)
                        await health(sibling)
                    assert await metadata() == identity, "Request identity changed with the UDP path"
                    for token, status in ((b"invalid", b"401"), (b"writer", b"403"), (b"reader", b"200")):
                        _, fields, body = await peer.request("/secure", headers=[(b"authorization", b"Bearer " + token)])
                        assert fields[b":status"] == status
                        if status == b"200":
                            assert body == b"reader"
                    pending_abort.cancel()
                    peer._quic.stop_stream(blocked, 0x10c)
                    peer._quic.reset_stream(blocked, 0x10c)
                    peer.transmit()
                    await state(b"aborted")
                    await peer.ping()
                    fallback = relay.current
                    await relay.rebind()
                    blackhole = relay.current
                    blackhole.blocked = True
                    before = fallback.received
                    stage = "failed-candidate-fallback"
                    await asyncio.wait_for(peer.ping(), 6)
                    assert blackhole.dropped > 0 and fallback.received > before, "Fallback path did not carry traffic"
                    assert blackhole.received_bytes <= 3 * blackhole.sent_bytes, "Failed candidate exceeded its anti-amplification budget"
                    relay.current = fallback
                    stage = "fallback-controller"
                    await health(peer)
                    await health(sibling)
                stage = "drain"
                _, pending = sibling.start_request("/drain")
                host.stdin.write(b"drain\n")
                await host.stdin.drain()
                fields, body = await asyncio.wait_for(pending, 8)
                assert dict(fields)[b":status"] == b"200" and body == b"d" * 131072
        stage = "host-cleanup"
        stdout, stderr = await asyncio.wait_for(host.communicate(), 10)
        communicated = True
        reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
        report = reports[-1] if reports else {}
        assert host.returncode == 0 and report.get("completed") and report.get("drainCompleted") and report.get("rebind"), report
        assert "active=0;" in report.get("transportDiagnostics", "") and "issued-routes=0;" in report.get("transportDiagnostics", ""), report
        stage = "completed"
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        if host.returncode is None:
            host.kill()
        if not communicated:
            stdout, stderr = await host.communicate()
    finally:
        if relay is not None:
            backend_receipts = [backend.receipt() for backend in relay.backends]
            relay.close()
    (output / "host.stdout.log").write_bytes(startup + stdout)
    (output / "host.stderr.log").write_bytes(stderr)
    (output / "peer.qlog").write_text(json.dumps(trace.to_dict()), encoding="utf-8")
    result = {"passed": failure is None, "failure": failure, "stage": stage, "cipher": cipher, "mode": mode,
              "healthyRequests": healthy, "requestIdentity": identity.decode() if healthy else None, "backends": backend_receipts, "exit": host.returncode, "server": report}
    (output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("passed", "failure", "stage", "cipher", "mode", "healthyRequests", "backends")}), flush=True)
    return result


async def main(args):
    lock = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    expected = [line.split("==", 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith("aioquic==")]
    assert len(expected) == 1 and importlib.metadata.version("aioquic") == expected[0]
    args.output.mkdir(parents=True, exist_ok=False)
    ciphers = ("AES_128_GCM_SHA256", "AES_256_GCM_SHA384") + (() if os.name == "nt" else ("CHACHA20_POLY1305_SHA256",))
    cases = []
    for cipher in ciphers:
        for mode in ("nat", "cid-ip"):
            cases.append(await case(args, cipher, mode))
            if not cases[-1]["passed"]:
                break
        if not cases[-1]["passed"]:
            break
    result = {**provenance(args), "passed": len(cases) == 2 * len(ciphers) and all(case["passed"] for case in cases),
              "cases": cases, "scope": "Controlled same-IP NAT and simultaneous source-port/IP/destination-CID changes with full-size probes, failed-candidate amplification/fallback, HTTP/3 controller, auth, identity and request-abort continuity, independent sibling progress and complete drain/rebind cleanup."}
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
