"""Compare HTTP/3 write framing over real loopback QUIC using identical source snapshots."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess

ROOT = Path(__file__).resolve().parents[1]
EXCHANGE = "src/EmbedIO/Net/Internal/Http3/Http3QuicExchange.cs"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--baseline", default="d40d83923478ed23d180403265e226ac2a155f32")
parser.add_argument("--output", type=Path, default=ROOT / "TestResults/http3-write-comparison")
parser.add_argument("--processes", type=int, default=3)
args = parser.parse_args()
if not 1 <= args.processes <= 10:
    parser.error("processes must be between 1 and 10")
out = args.output.resolve()
out.relative_to((ROOT / "TestResults").resolve())
out.mkdir(parents=True, exist_ok=True)
commit = subprocess.check_output(["git", "rev-parse", args.baseline], cwd=ROOT, text=True).strip()
original = subprocess.check_output(["git", "show", f"{commit}:{EXCHANGE}"], cwd=ROOT)
tracked = subprocess.check_output(["git", "ls-files", "src/EmbedIO"], cwd=ROOT, text=True).splitlines()
shared = ["Directory.Build.props", "Directory.Packages.props", ".editorconfig", "global.json", "LICENSE", "README.md",
          "src/AnalyzerUtilities.cs", "src/NullableAttributes.cs", "images/embedio_neo_icons.png"]
manifest = {"baseline_commit": commit, "method": "Identical current core sources except baseline HTTP/3 exchange from specified commit. Real loopback QUIC and HttpClient share a process. TLS setup/warmup excluded; body validation included. Three within-process samples, alternating variant process order, stable JIT. CPU/allocation cover both server and client; native allocation is not counted.", "process_pairs": args.processes, "variants": {}}
binaries = {}
for variant in ("baseline", "candidate"):
    directory = out / variant
    hashes = {}
    for name in tracked + shared:
        data = original if variant == "baseline" and name == EXCHANGE else (ROOT / name).read_bytes()
        target = directory / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        hashes[name] = hashlib.sha256(data).hexdigest()
    runner = directory / "runner"
    runner.mkdir(exist_ok=True)
    (runner / "Program.cs").write_bytes((ROOT / "test/EmbedIO.Performance/Http3WriteBenchmark.cs.txt").read_bytes())
    (runner / "WriteBenchmark.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="../src/EmbedIO/EmbedIO.csproj" /></ItemGroup></Project>')
    with (directory / "build.log").open("w") as log:
        subprocess.run(["dotnet", "build", str(runner / "WriteBenchmark.csproj"), "-c", "Release"], cwd=directory, stdout=log, stderr=subprocess.STDOUT, check=True)
    binary = runner / "bin/Release/net10.0/WriteBenchmark.dll"
    binaries[variant] = binary
    manifest["variants"][variant] = {"source_sha256": hashes, "runner_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
                                     "core_sha256": hashlib.sha256((binary.parent / "EmbedIO.dll").read_bytes()).hexdigest()}
(out / "dotnet-info.txt").write_bytes(subprocess.check_output(["dotnet", "--info"], cwd=ROOT))
(out / "sources.json").write_text(json.dumps(manifest, indent=2))
env = dict(os.environ, DOTNET_TieredCompilation="0")
rows = []
for process in range(args.processes):
    for variant in (("baseline", "candidate") if process % 2 == 0 else ("candidate", "baseline")):
        target = out / f"{variant}-{process}.json"
        with target.open("w") as log:
            subprocess.run(["dotnet", str(binaries[variant])], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        rows.extend(dict(row, variant=variant) for row in json.loads(target.read_text()))
summary = []
for size in (128, 65536, 1048576, 8388608):
    for concurrency in (1, 8):
        for variant in ("baseline", "candidate"):
            selected = [row for row in rows if row["Size"] == size and row["Concurrency"] == concurrency and row["variant"] == variant]
            summary.append(dict(size=size, concurrency=concurrency, variant=variant, samples=len(selected),
                median_mib_per_second=statistics.median(row["Size"] * row["Count"] / row["Seconds"] / 1048576 for row in selected),
                median_cpu_ms_per_response=statistics.median(row["CpuMilliseconds"] / row["Count"] for row in selected),
                median_allocated_bytes_per_response=statistics.median(row["AllocatedBytes"] / row["Count"] for row in selected),
                median_p50_ms=statistics.median(row["P50Milliseconds"] for row in selected),
                median_p95_ms=statistics.median(row["P95Milliseconds"] for row in selected),
                median_p99_ms=statistics.median(row["P99Milliseconds"] for row in selected)))
(out / "summary.json").write_text(json.dumps(summary, indent=2))
print(json.dumps(summary, indent=2))
