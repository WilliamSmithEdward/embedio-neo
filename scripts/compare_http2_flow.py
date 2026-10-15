"""Compare baseline/current HTTP/2 flow schedulers in one stable-JIT runner."""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess

ROOT = Path(__file__).resolve().parents[1]
FLOW = "src/EmbedIO/Net/Internal/Http2/Http2SendFlowControl.cs"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--baseline", default="759db37b396c4727f6fbb63540aed7b2126fcd2b")
parser.add_argument("--output", type=Path, default=ROOT / "TestResults/http2-flow-comparison")
args = parser.parse_args()
out = args.output.resolve()
out.relative_to((ROOT / "TestResults").resolve())
out.mkdir(parents=True, exist_ok=True)
base_commit = subprocess.check_output(["git", "rev-parse", args.baseline], cwd=ROOT, text=True).strip()
baseline = subprocess.check_output(["git", "show", f"{base_commit}:{FLOW}"], cwd=ROOT).decode("utf-8-sig")
candidate = (ROOT / FLOW).read_text(encoding="utf-8-sig")
(out / "Baseline.cs").write_text(baseline.replace("Http2SendFlowControl", "BaselineSendFlowControl"), encoding="utf-8-sig")
(out / "Candidate.cs").write_text(candidate, encoding="utf-8-sig")
(out / "HttpPriority.cs").write_bytes((ROOT / "src/EmbedIO/Net/Internal/HttpPriority.cs").read_bytes())
frame = (ROOT / "src/EmbedIO/Net/Internal/Http2/Http2Frame.cs").read_text(encoding="utf-8-sig")
(out / "Http2ProtocolException.cs").write_text(frame[:frame.index("    internal sealed class Http2Frame")] + "}\n", encoding="utf-8-sig")
(out / "Program.cs").write_bytes((ROOT / "test/EmbedIO.Performance/Http2FlowBenchmark.cs.txt").read_bytes())
(out / "Scheduler.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
(out / "Labels.resx").write_text('<root><data name="Header" xml:space="preserve"><value>sample,variant,workload,iterations,ns_per_iteration,bytes_per_iteration</value></data></root>')
with (out / "build.log").open("w") as log:
    subprocess.run(["dotnet", "build", str(out / "Scheduler.csproj"), "-c", "Release"], cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
binary = out / "bin/Release/net10.0/Scheduler.dll"
manifest = {"baseline_commit": base_commit, "candidate_utf8_sha256": hashlib.sha256(candidate.encode()).hexdigest(),
            "baseline_utf8_sha256": hashlib.sha256(baseline.encode()).hexdigest(),
            "runner_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
            "method": "Baseline class identifier renamed; candidate source unchanged. Both compiled in one runner. Three processes, five alternating samples each; DOTNET_TieredCompilation=0. Source hashes normalize BOM/newlines."}
(out / "sources.json").write_text(json.dumps(manifest, indent=2))
env = dict(os.environ, DOTNET_TieredCompilation="0")
rows = []
for process in range(1, 4):
    target = out / f"process-{process}.csv"
    with target.open("w") as log:
        subprocess.run(["dotnet", str(binary)], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
    with target.open() as log:
        rows.extend(csv.DictReader(log))
summary = []
for workload in ("writable", "blocked-8", "blocked-128"):
    for variant in ("baseline", "candidate"):
        chosen = [row for row in rows if row["workload"] == workload and row["variant"] == variant]
        summary.append({"workload": workload, "variant": variant, "samples": len(chosen),
                        "median_ns": statistics.median(float(row["ns_per_iteration"]) for row in chosen),
                        "median_bytes": statistics.median(float(row["bytes_per_iteration"]) for row in chosen)})
(out / "summary.json").write_text(json.dumps(summary, indent=2))
print(json.dumps(summary, indent=2))
