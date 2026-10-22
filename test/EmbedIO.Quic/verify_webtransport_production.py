"""WebTransport through the public WebServer API, checked by the pinned independent quic-go peer.

Each case starts the development host's serve-webtransport command, an ordinary
WebServer in EmbedIOHttp3 mode that echoes streams and datagrams through
IWebTransportContext, runs one peer mode against it, then closes the host's
standard input and checks the report the host prints.
"""
import argparse
import asyncio
import hashlib
import json
import os
from pathlib import Path
import subprocess

# The host's report must satisfy each mode's expectations, keyed by report field.
EXPECTATIONS = {
    "echo": {"acceptedSessions": 1, "bidirectionalEchoes": 1, "unidirectionalEchoes": 1, "datagramsEchoed": 1},
    "siblings": {"acceptedSessions": 2, "bidirectionalEchoes": 2, "unidirectionalEchoes": 2, "datagramsEchoed": 2},
    "reset": {"acceptedSessions": 1, "streamErrors": 1, "bidirectionalEchoes": 1, "unidirectionalEchoes": 1},
    "outbound-reset": {"acceptedSessions": 1},
    "session-fault": {"acceptedSessions": 2, "bidirectionalEchoes": 1, "unidirectionalEchoes": 1},
    "unauthorized": {"acceptedSessions": 0, "refusedSessions": 1},
    "origin": {"acceptedSessions": 0, "refusedSessions": 1},
    "missing-reset": {"acceptedSessions": 0, "refusedSessions": 0},
    "large": {"acceptedSessions": 1, "bidirectionalEchoes": 1, "unidirectionalEchoes": 1},
    "credit": {"acceptedSessions": 1, "bidirectionalEchoes": 1, "unidirectionalEchoes": 1},
    "datagram-fault": {"acceptedSessions": 1, "bidirectionalEchoes": 1},
    "idle": {"acceptedSessions": 1},
    "sustained": {"acceptedSessions": 1, "bidirectionalEchoes": 1, "unidirectionalEchoes": 1, "datagramsEchoed": 1},
    "sustained-credit": {"acceptedSessions": 1, "bidirectionalEchoes": 1, "unidirectionalEchoes": 1},
    "stream-cycle": {"acceptedSessions": 1, "bidirectionalEchoes": 128, "unidirectionalEchoes": 128, "datagramsEchoed": 1},
}
# Exact values; every other expectation is a minimum.
EXACT = {"acceptedSessions", "refusedSessions"}


async def case(args, mode):
    server = await asyncio.create_subprocess_exec(
        args.dotnet, args.assembly, "serve-webtransport",
        str(args.output / "certificate.pem"), str(args.output / "key.pem"),
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    peer = None
    line = b""
    peer_out = peer_err = stdout = stderr = b""
    failure = None
    try:
        line = await asyncio.wait_for(server.stdout.readline(), 15)
        ready = json.loads(line)
        environment = dict(os.environ, EMBEDIO_QUIC_PROBE_TOKEN=ready["authorization"])
        peer = await asyncio.create_subprocess_exec(
            args.peer, "-port", str(ready["port"]), "-authority", f"localhost:{ready['port']}",
            "-ca", str(args.output / "certificate.pem"), "-mode", mode,
            env=environment, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        peer_out, peer_err = await asyncio.wait_for(peer.communicate(), 30)
    except (TimeoutError, ValueError, OSError, KeyError) as error:
        failure = f"{type(error).__name__}: {error}"
    finally:
        if peer is not None and peer.returncode is None:
            peer.kill()
            peer_out, peer_err = await peer.communicate()
        try:
            server.stdin.close()
            stdout, stderr = await asyncio.wait_for(server.communicate(), 30)
        except TimeoutError:
            server.kill()
            stdout, stderr = await server.communicate()
            failure = failure or "server deadline exceeded"
    # The token exists only in the pipe and environment, never in retained logs.
    if line:
        try:
            redacted = json.loads(line)
            redacted.pop("authorization", None)
            line = json.dumps(redacted).encode() + b"\n"
        except ValueError:
            line = b"invalid startup report\n"
    for suffix, data in (("server.stdout", line + stdout), ("server.stderr", stderr),
                         ("peer.stdout", peer_out), ("peer.stderr", peer_err)):
        (args.output / (mode + "." + suffix + ".log")).write_bytes(data)
    reports = [json.loads(item) for item in stdout.splitlines() if item.startswith(b"{")]
    report = reports[-1] if reports else {}
    peer_reports = [json.loads(item) for item in peer_out.splitlines() if item.startswith(b"{")]
    peer_report = peer_reports[-1] if peer_reports else {}
    unmet = []
    for name, expected in EXPECTATIONS[mode].items():
        actual = report.get(name)
        if not isinstance(actual, int) or (actual != expected if name in EXACT else actual < expected):
            unmet.append(f"{name}={actual!r} expected {'' if name in EXACT else '>='}{expected}")
    passed = (failure is None and peer is not None and peer.returncode == 0 and peer_report.get("passed") is True
              and server.returncode == 0 and not unmet)
    return {"name": mode, "passed": passed, "failure": failure, "unmet": unmet, "serverExit": server.returncode,
            "peerExit": None if peer is None else peer.returncode, "server": report, "peer": peer_report}


async def main(args):
    args.output.mkdir(parents=True, exist_ok=False)
    subprocess.run([args.dotnet, args.assembly, "certificate", str(args.output)], check=True, capture_output=True)
    modes = args.modes or list(EXPECTATIONS)
    results = []
    for mode in modes:
        result = await case(args, mode)
        results.append(result)
        print(json.dumps(result), flush=True)
    receipt = {"passed": all(r["passed"] for r in results), "cases": results,
               "assemblySha256": hashlib.sha256(Path(args.assembly).read_bytes()).hexdigest(),
               "peerSha256": hashlib.sha256(Path(args.peer).read_bytes()).hexdigest()}
    (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("dotnet", "assembly", "peer"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--modes", nargs="*", help="Peer modes to run; all by default.")
    raise SystemExit(asyncio.run(main(parser.parse_args())))
