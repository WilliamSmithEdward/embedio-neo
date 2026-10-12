"""Issue #282: preserve bounded isolated profiles and unprofiled A/A cost controls."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

from summarize_teardown_exceptions import complete_sample, difference, phases

SCENARIOS = ("h1-plain-small-c64-close100", "h1-tls-small-c64", "h2-plain-small-c64x1",
             "h2-tls-small-c64x1", "h2-tls-churn-c32", "h3-small-c64x1", "h3-churn-c16")


def validate(root, census, duration, rounds):
    root = Path(root)
    if any((root / marker).exists() for marker in ("INVALID.txt", "ABORTED.txt")):
        raise ValueError("Campaign explicitly marked invalid or aborted")
    files = list((root / "samples").glob("*/*.json"))
    if len(files) != len(SCENARIOS) * 2 * rounds:
        raise ValueError("Missing or additional campaign samples")
    seen = set()
    rows = []
    for path in sorted(files):
        sample = json.loads(path.read_text())
        key = (sample["scenario"], sample["engine"], sample["round"])
        if key in seen or key[0] not in SCENARIOS or key[2] not in range(1, rounds + 1):
            raise ValueError("Unexpected or duplicate sample identity")
        seen.add(key)
        if not complete_sample(sample):
            raise ValueError("Failed sample or unsuccessful cleanup: " + str(path))
        identity = sample["server"]
        for assembly in identity["assemblies"]:
            value = assembly["sha256"]
            if not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdefABCDEF" for c in value):
                raise ValueError("Missing assembly identity")
        if identity["protocol"] == "Http3":
            value = (identity.get("msquic") or {}).get("sha256")
            if not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdefABCDEF" for c in value):
                raise ValueError("Actual loaded MsQuic identity unavailable")
        client = sample["client"]
        if client["requests"] != math.ceil(32 * duration) or client["latencyCount"] != client["requests"]:
            raise ValueError("Paced requests or latency samples missing: " + str(path))
        if sample["requestRate"] != 32 or sample["connectionCensus"] != census or sample["exceptionCensus"] != census or identity["exceptionCensus"] != census:
            raise ValueError("Configuration changed")
        measured = sample["machineMeasurementSeconds"]
        if not math.isfinite(measured) or measured <= 0:
            raise ValueError("Invalid machine CPU acquisition interval")
        background = sample["derived"]["backgroundCpuSeconds"] / measured
        if not math.isfinite(background) or background > 0.5:
            raise ValueError("Quiet sample contaminated: " + str(path))
        counts = phases(sample) if census else None
        site_phases = None
        if census:
            final = sample["serverStop"]["connections"]
            if not final["instrumentSeen"] or final["invalidMeasurements"] or final["active"] != 0:
                raise ValueError("Server connection accounting unavailable or incomplete")
            if final["opened"] <= 0 or final["closed"] != final["opened"]:
                raise ValueError("Server accepted/closed connections do not balance")
            # One worker can create at most one final empty connection per phase.
            # An excess catches exhausted pacing loops even when all bodies validate.
            if final["opened"] > client["requests"] + sample["warmup"]["requests"] + 2:
                raise ValueError("Unexpected zero-request connection churn")
            before, after = sample["connectionsBeforeLoad"], sample["connectionsBeforeStop"]
            closed = after["closed"] - before["closed"]
            points = []
            for point in ("AtReady", "BeforeLoad", "AfterLoad", "BeforeStop"):
                site = sample["exceptionSites" + point]
                if site is None or site["overflow"]:
                    raise ValueError("Exception site capture unavailable or overflowed")
                if sum(site["counts"].values()) != sum(site["types"].values()):
                    raise ValueError("Atomic exception site/type census disagrees")
                points.append(site)
            stop_site = sample["serverStop"]["exceptionSites"]
            if stop_site["overflow"] or sum(stop_site["counts"].values()) != sum(stop_site["types"].values()):
                raise ValueError("Incomplete stop site census")
            points.append(stop_site)
            names = ("warmup-and-settle", "load-and-client-close", "post-load-idle", "listener-stop")
            site_phases = {name: difference(after["counts"], before["counts"]) for name, before, after in zip(names, points, points[1:])}
            counts = {name: difference(after["types"], before["types"]) for name, before, after in zip(names, points, points[1:])}
        else:
            closed = None
        rows.append({"sample": str(path.relative_to(root)), "requests": client["requests"],
                     "clientConnectionsOpened": client["connectionsOpened"], "serverClosedDuringLoadAndIdle": closed,
                     "exceptions": counts, "stackSites": site_phases, "metrics": sample["derived"], "p99Milliseconds": client["p99Milliseconds"],
                     "backgroundBusyCpus": background})
    expected_engines = {"candidate", "kestrel"} if census else None
    engines = {key[1] for key in seen}
    if expected_engines is not None and engines != expected_engines:
        raise ValueError("Missing comparison engine")
    if not census and engines not in ({"candidate", "kestrel"}, {"candidate", "baseline"}):
        raise ValueError("Unknown cost engines")
    expected = {(s, e, r) for s in SCENARIOS for e in engines for r in range(1, rounds + 1)}
    if seen != expected:
        raise ValueError("Incomplete scenario/engine/round matrix")
    return rows



def validate_recovery(path):
    report = json.loads(Path(path).read_text())
    if report.get("error") or report.get("stopError") or report["verdict"]:
        raise ValueError("Recovery reports a genuine failure")
    if report["serverExitCode"] != 0 or report["serverKilled"] is not False:
        raise ValueError("Recovery server did not exit cleanly")
    for name in ("healthy-before", "healthy-after"):
        if report[name + "ClientKilled"] is not False:
            raise ValueError("Recovery client required forced cleanup")
        phase = next(p for p in report["phases"] if p["phase"] == name)
        if phase["client"].get("error") or phase["client"]["requests"] <= 0:
            raise ValueError("Missing validated healthy recovery traffic")
    snapshots = [p["exceptionSites"] for p in report["phases"] if "exceptionSites" in p]
    snapshots.append(report["stop"]["exceptionSites"])
    for site in snapshots:
        if site is None or site["overflow"] or sum(site["counts"].values()) != sum(site["types"].values()):
            raise ValueError("Recovery site census incomplete")
    final = report["stop"]["connections"]
    if not final["instrumentSeen"] or final["invalidMeasurements"] or final["active"] != 0 or final["opened"] <= 0 or final["opened"] != final["closed"]:
        raise ValueError("Recovery server connection accounting incomplete")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runner", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true" or sys.platform != "linux":
        raise SystemExit("Sustained profiling uses disposable GitHub runners only.")
    args.output.mkdir(parents=True, exist_ok=False)
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    manifest = {"revision": revision, "run": os.environ.get("GITHUB_RUN_ID"), "attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
                "runnerHash": hashlib.sha256(args.runner.read_bytes()).hexdigest(),
                "coreHash": hashlib.sha256((args.runner.parent / "EmbedIO.dll").read_bytes()).hexdigest(), "commands": []}
    copy = args.output / "identical-runner"
    shutil.copytree(args.runner.parent, copy)
    manifest["aaCoreHash"] = hashlib.sha256((copy / "EmbedIO.dll").read_bytes()).hexdigest()
    if manifest["aaCoreHash"] != manifest["coreHash"]:
        raise ValueError("A/A core identity differs")
    failures = []
    time.sleep(30)
    for arm, engines, duration, rounds, census in (("profile", "candidate,kestrel", 5, 2, True),
                                                   ("aa", "candidate,baseline", 15, 3, False),
                                                   ("cost", "candidate,kestrel", 15, 3, False)):
        command = ["dotnet", str(args.runner), "run", "--output", str(args.output / arm),
                   "--engines", engines, "--scenarios", ",".join(SCENARIOS), "--connections", "1", "--streams", "1",
                   "--rounds", str(rounds), "--warmup", "2", "--duration", str(duration), "--idle", "2",
                   "--request-rate", "32", "--max-baseline-busy-cpus", "0.5", "--candidate-revision", revision]
        if arm == "aa": command.extend(["--baseline-dir", str(copy), "--modern-baseline", "--baseline-revision", revision])
        if census: command.extend(["--connection-census", "--profile"])
        else: command.append("--disable-exception-census")
        manifest["commands"].append(command)
        (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2))
        environment = dict(os.environ)
        if census: environment["EMBEDIO_BENCH_EXCEPTION_DETAIL"] = "1"
        else: environment.pop("EMBEDIO_BENCH_EXCEPTION_DETAIL", None)
        with (args.output / (arm + ".log")).open("w") as log:
            try:
                child = subprocess.Popen(command, env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                try:
                    code = child.wait(timeout=1500)
                except subprocess.TimeoutExpired:
                    os.killpg(child.pid, signal.SIGKILL)
                    child.wait()
                    raise
                if code:
                    raise subprocess.CalledProcessError(code, command)
                rows = validate(args.output / arm, census, duration, rounds)
                (args.output / (arm + "-validated.json")).write_text(json.dumps(rows, indent=2))
            except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
                failures.append(arm + ": " + str(error))
    for protocol, tls in (("Http1", False), ("Http1", True), ("Http2", True), ("Http3", True)):
        for engine in ("embedio", "kestrel"):
            name = f"fault-{protocol}-{tls}-{engine}"
            command = ["dotnet", str(args.runner), "recovery", "--output", str(args.output / name), "--engine", engine,
                       "--protocol", protocol, "--connections", "1", "--streams", "1", "--healthy-seconds", "5",
                       "--storm-seconds", "5", "--storm-workers", "2", "--idle", "2", "--exception-phases"]
            if tls: command.append("--tls")
            manifest["commands"].append(command)
            (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2))
            environment = dict(os.environ, EMBEDIO_BENCH_EXCEPTION_DETAIL="1")
            with (args.output / (name + ".log")).open("w") as log:
                try:
                    child = subprocess.Popen(command, env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                    try:
                        code = child.wait(timeout=180)
                    except subprocess.TimeoutExpired:
                        os.killpg(child.pid, signal.SIGKILL)
                        child.wait()
                        raise
                    if code: raise subprocess.CalledProcessError(code, command)
                    validate_recovery(args.output / name / "recovery.json")
                except (ValueError, KeyError, StopIteration, OSError, subprocess.SubprocessError) as error:
                    failures.append(name + ": " + str(error))
    (args.output / "verdict.json").write_text(json.dumps({"passed": not failures, "failures": failures}, indent=2))
    for failure in failures: print(failure)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
