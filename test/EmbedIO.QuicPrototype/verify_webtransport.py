"""Current-wire WebTransport tests using a pinned independent quic-go transport."""
import argparse
import asyncio
import hashlib
import json
import os
from pathlib import Path
import subprocess
from verify import DatagramRelay, provenance

async def case(args, mode, relay=None):
    server = await asyncio.create_subprocess_exec(args.dotnet, args.assembly, "serve-h3", args.openssl,
        str(args.output / "certificate.pem"), str(args.output / "key.pem"),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    relay_transport = None
    peer = None
    line = b""
    peer_out = peer_err = b""
    failure = None
    try:
        line = await asyncio.wait_for(server.stdout.readline(), 5)
        ready = json.loads(line)
        port = ready["port"]
        if relay:
            relay_transport, _ = await asyncio.get_running_loop().create_datagram_endpoint(
                lambda: DatagramRelay(("127.0.0.1", port), relay), local_addr=("127.0.0.1", 0))
            port = relay_transport.get_extra_info("sockname")[1]
        environment = dict(os.environ, EMBEDIO_QUIC_PROBE_TOKEN=ready["authorization"])
        peer = await asyncio.create_subprocess_exec(args.peer, "-port", str(port), "-ca",
            str(args.output / "certificate.pem"), "-mode", mode, env=environment,
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        peer_out, peer_err = await asyncio.wait_for(peer.communicate(), 12)
    except (TimeoutError, ValueError, OSError) as error:
        failure = str(error)
    finally:
        if peer is not None and peer.returncode is None:
            peer.kill()
            peer_out, peer_err = await peer.communicate()
        try:
            stdout, stderr = await asyncio.wait_for(server.communicate(), 12)
        except TimeoutError:
            server.kill()
            stdout, stderr = await server.communicate()
            failure = "server deadline exceeded"
        if relay_transport is not None:
            relay_transport.close()
    name = mode + ("-" + relay if relay else "")
    # The one-process token exists only in the pipe/environment, not retained logs.
    if line:
        try:
            redacted = json.loads(line)
            redacted.pop("authorization", None)
            line = json.dumps(redacted).encode() + b"\n"
        except ValueError:
            line = b"invalid startup report\n"
    for suffix, data in (("server.stdout", line + stdout), ("server.stderr", stderr),
                         ("peer.stdout", peer_out), ("peer.stderr", peer_err)):
        (args.output / (name + "." + suffix + ".log")).write_bytes(data)
    reports = [json.loads(item) for item in stdout.splitlines() if item.startswith(b"{")]
    report = reports[-1] if reports else {}
    negative = mode in ("unauthorized", "origin", "missing-reset")
    passed = failure is None and peer is not None and peer.returncode == 0 and server.returncode == (1 if negative else 0)
    passed = passed and report.get("acceptedSessions") == (0 if negative else 2 if mode in ("siblings", "session-fault") else 1)
    if mode == "outbound-reset":
        passed = passed and report.get("streamsDelivered", 0) == 1 and report.get("outboundResets", 0) >= 1
    elif not negative:
        passed = passed and report.get("streamsDelivered", 0) >= (4 if mode == "siblings" else 2) and report.get("datagramsDelivered", 0) >= 1
    if mode in ("large", "credit"):
        passed = passed and report.get("keyUpdates", 0) >= 1
    if mode == "reset":
        passed = passed and report.get("resetsDelivered", 0) == 1
    if relay in ("loss-handshake", "loss-stream"):
        passed = passed and report.get("retransmittedPackets", 0) > 0
    return {"name": name, "passed": passed, "failure": failure, "serverExit": server.returncode,
            "peerExit": None if peer is None else peer.returncode, "server": report,
            "peer": peer_out.decode(errors="replace")}

async def main(args):
    args.output.mkdir(parents=True, exist_ok=False)
    subprocess.run([args.dotnet, args.assembly, "certificate", str(args.output)], check=True, capture_output=True)
    results = []
    for mode, relay in [("echo", None), ("siblings", None), ("reset", None), ("outbound-reset", None), ("session-fault", None), ("unauthorized", None), ("origin", None),
                        ("missing-reset", None), ("large", None), ("credit", None),
                        ("echo", "loss-handshake"), ("echo", "loss-stream"), ("large", "loss-stream")]:
        result = await case(args, mode, relay)
        results.append(result)
        print(json.dumps(result), flush=True)
        if not result["passed"]:
            break
    receipt = {"passed": len(results) == 13 and all(r["passed"] for r in results), "cases": results,
               **provenance(args),
               "peerSha256": hashlib.sha256(Path(args.peer).read_bytes()).hexdigest()}
    (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    return 0 if receipt["passed"] else 1

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("dotnet", "assembly", "openssl", "peer"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--output", type=Path, required=True)
    raise SystemExit(asyncio.run(main(parser.parse_args())))
