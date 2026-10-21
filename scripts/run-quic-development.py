"""Extended local QUIC campaigns; CI keeps its bounded per-push budgets."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import shutil
import sys
import signal
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--rounds", type=int, default=20)
    parser.add_argument("--cases", type=int, default=100000)
    parser.add_argument("--state-cases", type=int, default=10000)
    parser.add_argument("--native-cases", type=int, default=5000)
    parser.add_argument("--coverage-seconds", type=int, default=600)
    parser.add_argument("--openssl", type=Path)
    parser.add_argument("--schannel", action="store_true", help="Include Windows native TLS and certificate-policy campaigns")
    args = parser.parse_args()
    if not (1 <= args.rounds <= 100 and 1 <= args.cases <= 100000 and 1 <= args.state_cases <= 100000
            and 1 <= args.native_cases <= 100000 and 0 <= args.coverage_seconds <= 1800):
        parser.error("Campaign counts or duration exceed supported bounds.")
    if args.schannel and os.name != "nt":
        parser.error("Schannel campaigns require Windows.")
    repo = Path(__file__).resolve().parent.parent
    os.chdir(repo)
    report = repo / "TestResults" / "quic-development" / uuid.uuid4().hex
    report.mkdir(parents=True)
    runs = []
    manifest = {"source": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
                "workingTree": subprocess.check_output(["git", "status", "--porcelain"], text=True).splitlines(),
                "configuration": {key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()},
                "runs": runs, "passed": False}
    files = sorted(path for folder in ("src/EmbedIO.Quic", "src/EmbedIO", "src/Shared", "test/EmbedIO.Quic", "test/EmbedIO.Fuzz")
                   for path in Path(folder).rglob("*.cs")
                   if not {"bin", "obj"}.intersection(path.parts))
    manifest["sourceHashes"] = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in files}

    def save():
        (report / "receipt.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    def run(name, command, timeout=300, cleanup=None):
        started = time.monotonic()
        record = {"name": name, "command": list(map(str, command))}
        runs.append(record)
        save()
        with (report / (name + ".log")).open("w", encoding="utf-8") as log:
            options = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
            with subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, **options) as process:
                try:
                    record["exit"] = process.wait(timeout=timeout)
                    record["passed"] = process.returncode == 0
                except subprocess.TimeoutExpired:
                    record["passed"] = False
                    record["timeout"] = timeout
                    if os.name == "nt":
                        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], stdout=log, stderr=subprocess.STDOUT, timeout=15)
                    else:
                        os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=15)
                    if cleanup:
                        subprocess.run(cleanup, stdout=log, stderr=subprocess.STDOUT, timeout=30)
        record["seconds"] = round(time.monotonic() - started, 3)
        save()
        print(name, "PASS" if record["passed"] else "FAIL", record["seconds"], flush=True)
        if not record["passed"]:
            raise RuntimeError(f"Campaign failed; evidence: {report}")

    print("Evidence:", report, flush=True)
    run("restore", [args.dotnet, "restore", "test/EmbedIO.Fuzz", "--locked-mode"])
    # Freeze managed harness outputs; subsequent editor builds cannot change a running campaign.
    harness = report / "harness"
    run("build", [args.dotnet, "build", "test/EmbedIO.Fuzz", "-c", "Release", "--no-restore"])
    shutil.copytree(repo / "test/EmbedIO.Fuzz/bin/Release/net10.0", harness,
                    ignore=shutil.ignore_patterns("EmbedIO.Quic.dll", "EmbedIO.Quic.pdb"))
    manifest["assemblyHashes"] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in harness.glob("*.dll")}
    save()
    fuzz = [args.dotnet, str(harness / "EmbedIO.Fuzz.dll")]
    native = [args.dotnet, str(harness / "EmbedIO.Quic.Development.dll")]
    run("self-test", native + ["self-test"])
    if args.openssl:
        run("certificate", native + ["certificate", str(report / "certificate")])
        run("production-http3", [sys.executable, "test/EmbedIO.Quic/verify_http3.py", "--dotnet", args.dotnet,
            "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--openssl", str(args.openssl.resolve()),
            "--output", str(report / "production-http3")])
        run("production-listener", [sys.executable, "test/EmbedIO.Quic/verify_listener.py", "--dotnet", args.dotnet,
            "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--openssl", str(args.openssl.resolve()),
            "--output", str(report / "production-listener")])
    if args.schannel:
        run("schannel-self-test", native + ["schannel-self-test"])
        for profile in ("schannel-rsa", "schannel-ecdsa"):
            run(profile + "-listener", [sys.executable, "test/EmbedIO.Quic/verify_listener.py", "--dotnet", args.dotnet,
                "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--openssl", profile,
                "--output", str(report / profile)])
        run("schannel-certificate-policy", [sys.executable, "test/EmbedIO.Quic/verify_tls_listener.py", "--dotnet", args.dotnet,
            "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--output", str(report / "certificate-policy")])
        run("schannel-client-certificate-policy", [sys.executable, "test/EmbedIO.Quic/verify_client_certificate.py", "--dotnet", args.dotnet,
            "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--output", str(report / "client-certificate-policy")])
    if args.schannel:
        run("peer-connection-ids", [sys.executable, "test/EmbedIO.Quic/verify_peer_connection_ids.py", "--dotnet", args.dotnet,
            "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--output", str(report / "peer-connection-ids")])
    if args.schannel:
        run("stateless-reset", [sys.executable, "test/EmbedIO.Quic/verify_stateless_reset.py", "--dotnet", args.dotnet,
            "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--output", str(report / "stateless-reset")])
    if args.openssl or args.schannel:
        cipher_command = [sys.executable, "test/EmbedIO.Quic/verify_cipher_suites.py", "--dotnet", args.dotnet,
                          "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--output", str(report / "cipher-suites")]
        if args.openssl:
            cipher_command += ["--openssl", str(args.openssl.resolve())]
        if args.schannel:
            cipher_command += ["--schannel"]
        run("cipher-suites", cipher_command)
    for index in range(args.rounds):
        seed = 1729 + index * 104729
        run(f"properties-{seed}", fuzz + ["--fscheck", str(seed), str(args.cases)])
        run(f"mutations-{seed}", fuzz + ["--quic-core", str(seed), str(args.cases)])
        for scenario in ("quic-recovery", "quic-lifecycle"):
            run(f"{scenario}-{seed}", fuzz + ["--state-machines", str(seed), str(args.state_cases), scenario])
        if args.openssl:
            run(f"native-{seed}", native + ["fuzz-native", str(args.openssl.resolve()),
                str(report / "certificate/certificate.pem"), str(report / "certificate/key.pem"),
                str(seed), str(args.native_cases), str(report / f"native-{seed}")])
        if args.schannel:
            for profile in ("schannel-rsa", "schannel-ecdsa"):
                directory = report / f"{profile}-{seed}"
                directory.mkdir()
                run(f"{profile}-native-{seed}", native + ["fuzz-native", profile,
                    str(directory / "certificate.pem"), str(directory / "unused-key.pem"),
                    str(seed), str(args.native_cases), str(directory / "native")])
            run(f"schannel-auth-{seed}", [sys.executable, "test/EmbedIO.Quic/fuzz_client_certificate.py", "--dotnet", args.dotnet,
                "--assembly", str(harness / "EmbedIO.Quic.Development.dll"), "--cases", str(args.native_cases),
                "--seed", str(seed), "--output", str(report / f"schannel-auth-{seed}")],
                timeout=max(30, min(1800, 30 + math.ceil(args.native_cases / 16))) + 60)
    if args.coverage_seconds:
        for script in ("run-sharpfuzz.ps1", "run-afl.ps1"):
            command = ["pwsh", "-NoProfile", "-File", "scripts/" + script,
                       "-Seconds", str(args.coverage_seconds), "-Seed", "1729"]
            cleanup = None
            if script == "run-afl.ps1":
                container = "embedio-afl-" + uuid.uuid4().hex
                command.extend(["-ContainerName", container])
                cleanup = ["docker", "stop", "--time", "5", container]
            run(script, command, args.coverage_seconds + 900, cleanup)
    manifest["passed"] = True
    save()
    print("Completed; receipt:", report / "receipt.json", flush=True)


if __name__ == "__main__":
    main()
