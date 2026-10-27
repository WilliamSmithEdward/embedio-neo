"""Shared-port QUIC ownership and cancellation checks with independent clients."""
import argparse
import asyncio
import json
import hashlib
import importlib.metadata
import os
from pathlib import Path
import socket
import ssl
import subprocess
from verify import ALPN, Peer, DatagramRelay, provenance
from quic_client import connect
from aioquic.quic.configuration import QuicConfiguration


async def parallel(operations):
    async with asyncio.TaskGroup() as group:
        for operation in operations:
            group.create_task(operation)


async def echo(args, port, index, barrier=None, relay=None, length=4096, server_closes=False):
    transport = None
    if relay:
        transport, _ = await asyncio.get_running_loop().create_datagram_endpoint(
            lambda: DatagramRelay(("127.0.0.1", port), relay), local_addr=("127.0.0.1", 0))
        port = transport.get_extra_info("sockname")[1]
    configuration = QuicConfiguration(is_client=True, alpn_protocols=[ALPN], server_name="localhost",
                                    verify_mode=ssl.CERT_REQUIRED, idle_timeout=3)
    configuration.load_verify_locations(cafile=str(args.output / "certificate.pem"))
    try:
        async with asyncio.timeout(7):
            async with connect("127.0.0.1", port, configuration=configuration, create_protocol=Peer) as peer:
                if barrier:
                    await barrier.wait()
                reader, writer = await peer.create_stream()
                payload = bytes((value + index) % 251 for value in range(length))
                writer.write(payload)
                writer.write_eof()
                if await reader.read() != payload:
                    raise AssertionError("Shared listener returned another owner's bytes")
                if server_closes:
                    await peer.wait_closed()
                    if peer.termination is None or peer.termination["code"] != 0x1234:
                        raise AssertionError("Async server did not close after acknowledged echo completion")
    finally:
        if transport:
            transport.close()


async def run(args, name, mode, expected, operation, port=0, timed_out=False):
    server = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, mode, args.openssl,
        str(args.output / "certificate.pem"), str(args.output / "key.pem"), str(expected), str(port),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    startup = b""
    failure = None
    try:
        startup = await asyncio.wait_for(server.stdout.readline(), 5)
        ready = json.loads(startup)
        await operation(ready)
        stdout, stderr = await asyncio.wait_for(server.communicate(), 12)
    except (Exception, asyncio.CancelledError) as error:
        failure = repr(error)
        if server.returncode is None:
            server.kill()
        stdout, stderr = await server.communicate()
    ready = json.loads(startup) if startup else {}
    ready.pop("authorization", None)
    (args.output / (name + ".stdout.log")).write_bytes(json.dumps(ready).encode() + b"\n" + stdout)
    (args.output / (name + ".stderr.log")).write_bytes(stderr)
    reports = [json.loads(line) for line in stdout.splitlines() if line.startswith(b"{")]
    report = reports[-1] if reports else {}
    passed = failure is None and server.returncode == (1 if timed_out else 0)
    passed = passed and report.get("active") == 0 and report.get("retired") == report.get("accepted")
    passed = passed and report.get("deadline") is timed_out and report.get("failedOwners") == 0
    if not timed_out:
        passed = passed and report.get("completed") == expected and report.get("peak", 0) >= min(expected, 2)
    result = {"name": name, "passed": passed, "failure": failure, "server": report, "port": ready.get("port")}
    print(json.dumps(result), flush=True)
    return result


async def main(args):
    lock = Path(__file__).resolve().parents[1] / "EmbedIO.Conformance/drivers/requirements.txt"
    pins = [line.split("==", 1)[1].split()[0] for line in lock.read_text().splitlines() if line.startswith("aioquic==")]
    if len(pins) != 1 or importlib.metadata.version("aioquic") != pins[0]:
        raise RuntimeError("Independent peer does not match the repository dependency lock")
    args.output.mkdir(parents=True, exist_ok=False)
    subprocess.run([args.dotnet, args.assembly, "certificate", str(args.output)], check=True, capture_output=True)
    async def concurrent(ready):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as noise:
            for _ in range(50):
                noise.sendto(b"bad packet", ("127.0.0.1", ready["port"]))
        barrier = asyncio.Barrier(8)
        await parallel(echo(args, ready["port"], index, barrier,
            "tamper" if index == 0 else "loss-handshake" if index == 1 else None) for index in range(8))

    results = [await run(args, "concurrent-noise-loss", "serve-many", 8, concurrent)]
    async def waves(ready):
        for wave in range(3):
            barrier = asyncio.Barrier(4)
            await parallel(echo(args, ready["port"], wave * 4 + index, barrier) for index in range(4))
            if wave != 2:
                await asyncio.sleep(1.1)
    if results[-1]["passed"]:
        results.append(await run(args, "waves-rebind", "serve-many", 12, waves, results[-1]["port"]))

    async def webtransport(ready):
        async def peer(index, mode):
            process = await asyncio.create_subprocess_exec(args.peer, "-port", str(ready["port"]), "-ca",
                str(args.output / "certificate.pem"), "-mode", mode,
                env=dict(os.environ, EMBEDIO_QUIC_PROBE_TOKEN=ready["authorization"]),
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            try:
                stdout, stderr = await asyncio.wait_for(process.communicate(), 9)
            except BaseException:
                if process.returncode is None:
                    process.kill()
                await process.communicate()
                raise
            (args.output / f"shared-h3-peer-{index}.log").write_bytes(stdout + stderr)
            if process.returncode != 0:
                raise AssertionError(f"Independent H3 peer failed: {mode}")
        await parallel(peer(index, mode) for index, mode in enumerate(("echo", "reset", "session-fault", "sustained")))
    if results[-1]["passed"]:
        results.append(await run(args, "shared-webtransport", "serve-many-h3", 4, webtransport))

    async def scheduled_async(ready):
        barrier = asyncio.Barrier(8)
        await parallel(echo(args, ready["port"], index, barrier, length=131072, server_closes=True) for index in range(8))
    if results[-1]["passed"]:
        results.append(await run(args, "shared-async-streams", "serve-many-async", 8, scheduled_async))

    async def hold(ready):
        configuration = QuicConfiguration(is_client=True, alpn_protocols=[ALPN], server_name="localhost",
                                        verify_mode=ssl.CERT_REQUIRED, idle_timeout=30)
        configuration.load_verify_locations(cafile=str(args.output / "certificate.pem"))
        async with connect("127.0.0.1", ready["port"], configuration=configuration) as peer:
            # Keep an established owner live until the independent host deadline cancels it.
            for _ in range(19):
                await peer.ping()
                await asyncio.sleep(0.5)
            await asyncio.sleep(1)
    if results[-1]["passed"]:
        results.append(await run(args, "cancel-active-owner", "serve-many", 1, hold, timed_out=True))
    receipt = {**provenance(args), "peerSha256": hashlib.sha256(Path(args.peer).read_bytes()).hexdigest(),
               "aioquicVersion": importlib.metadata.version("aioquic"), "cases": results, "passed": len(results) == 5 and all(r["passed"] for r in results)}
    (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("dotnet", "assembly", "openssl", "peer"):
        parser.add_argument("--" + option, required=True)
    parser.add_argument("--output", required=True, type=Path)
    raise SystemExit(asyncio.run(main(parser.parse_args())))
