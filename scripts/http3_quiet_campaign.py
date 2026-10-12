"""Bounded disposable-runner HTTP/3 control; preserve every sample and fail closed."""
import argparse
import hashlib
import json
import math
import os
import signal
from pathlib import Path
import subprocess


def validate(directory, revision, burn_threads):
    environment = json.loads((directory / "environment.json").read_text())
    if environment["sourceRevision"] != revision:
        raise ValueError("Source revision mismatch")
    for key in ("runnerSha256", "embedioSha256", "kestrelCoreSha256",
                "kestrelQuicSha256", "systemNetQuicSha256", "msquicSha256"):
        value = environment[key]
        if len(value) != 64 or any(c not in "0123456789abcdefABCDEF" for c in value):
            raise ValueError("Missing binary hash: " + key)
    if environment["handshakeTimeout"] != "runtime default":
        raise ValueError("Handshake deadline changed")
    if environment["burnThreads"] != burn_threads or environment["rounds"] != 2:
        raise ValueError("Campaign configuration mismatch")
    if environment["hostIdleBaseline"]["busyCpus"] > 0.5:
        raise ValueError("Host baseline is not quiet")
    rows = []
    for engine in ("embedio", "kestrel"):
        for round_number in (1, 2):
            run = json.loads((directory / f"{engine}-r{round_number}.json").read_text())
            if run["failureCount"] or run.get("error") or run.get("stopError"):
                raise ValueError("Campaign recorded a failure; inspect original JSON")
            if run["serverExitCode"] != 0 or run["serverKilled"]:
                raise ValueError("Server did not exit cleanly")
            phases = run["phases"]
            if [phase["rate"] for phase in phases] != environment["rates"]:
                raise ValueError("Missing or reordered rate phases")
            for phase in phases:
                expected = math.ceil(phase["rate"] * environment["phaseSeconds"])
                if phase["failureCount"] or phase["failures"] or phase["skippedAtCap"]:
                    raise ValueError("Failed or omitted churn attempts")
                if phase["started"] != expected or phase["completed"] != expected:
                    raise ValueError("Incomplete paced churn")
                for key in ("handshake", "firstStream", "close"):
                    if phase[key]["count"] != expected:
                        raise ValueError("Incomplete latency histogram: " + key)
                mixed = phase["mixed"]
                if mixed["failures"] or mixed["errors"]:
                    raise ValueError("Mixed traffic failed")
                for key, rate_key in (("small", "mixedRate"), ("large", "largeRate")):
                    if mixed[key]["count"] != math.ceil(environment[rate_key] * environment["phaseSeconds"]):
                        raise ValueError("Incomplete mixed traffic: " + key)
                samples = phase["timeline"]
                if len(samples) < environment["phaseSeconds"] - 2:
                    raise ValueError("Missing CPU timeline")
                host = sum(s["hostBusyCpus"] for s in samples) / len(samples)
                background = sum(s["backgroundCpus"] for s in samples) / len(samples)
                if not math.isfinite(host) or not math.isfinite(background):
                    raise ValueError("Invalid CPU measurement")
                if burn_threads == 0 and background > 0.5:
                    raise ValueError("Quiet phase was contaminated by background load")
                if burn_threads and host < environment["logicalProcessors"] * 0.8:
                    raise ValueError("Contention arm did not reach 80% measured host CPU")
                rows.append({"engine": engine, "round": round_number, "rate": phase["rate"],
                             "completed": expected, "handshake": phase["handshake"],
                             "firstStream": phase["firstStream"], "mixedSmall": mixed["small"],
                             "mixedLarge": mixed["large"], "hostBusyCpusMean": host,
                             "backgroundCpusMean": background})
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runner", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if not os.environ.get("GITHUB_ACTIONS") == "true":
        raise SystemExit("This all-core contention campaign is restricted to disposable GitHub runners.")
    args.output.mkdir(parents=True, exist_ok=False)
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    manifest = {"revision": revision, "githubRun": os.environ.get("GITHUB_RUN_ID"),
                "runner": str(args.runner.resolve()),
                "runnerSha256": hashlib.sha256(args.runner.read_bytes()).hexdigest(),
                "cpus": os.cpu_count(), "commands": []}
    failures = []
    for arm, rates, burn in (("quiet", "4,16,64,128", 0), ("contention", "64,128", os.cpu_count())):
        directory = args.output / arm
        command = ["dotnet", str(args.runner), "quic-churn", "--output", str(directory),
                   "--source-revision", revision, "--engines", "embedio,kestrel",
                   "--rounds", "2", "--rates", rates, "--phase-seconds", "15", "--idle", "2",
                   "--mixed-rate", "32", "--large-rate", "2", "--mixed-connections", "4",
                   "--baseline-seconds", "15", "--max-baseline-busy-cpus", "0.5",
                   "--burn-threads", str(burn)]
        manifest["commands"].append(command)
        (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        with (args.output / (arm + ".log")).open("w") as log:
            try:
                child = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT,
                                         start_new_session=os.name != "nt")
                try:
                    code = child.wait(timeout=600)
                except subprocess.TimeoutExpired:
                    # Kill only the process tree created for this campaign.
                    if os.name == "nt":
                        subprocess.run(["taskkill", "/PID", str(child.pid), "/T", "/F"],
                                       stdout=log, stderr=subprocess.STDOUT, check=False)
                    else:
                        os.killpg(child.pid, signal.SIGKILL)
                    child.kill()
                    child.wait()
                    raise
                if code:
                    failures.append(f"{arm}: harness exited {code}")
                rows = validate(directory, revision, burn)
                (args.output / (arm + "-validated.json")).write_text(json.dumps(rows, indent=2) + "\n")
            except (ValueError, KeyError, OSError, subprocess.TimeoutExpired) as error:
                failures.append(f"{arm}: {error}")
        # No repeated load or quieter retry to erase a failed campaign.
    (args.output / "verdict.json").write_text(json.dumps({"passed": not failures, "failures": failures}, indent=2) + "\n")
    for failure in failures:
        print(failure)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
