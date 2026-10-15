"""Build candidate and baseline runners for test/EmbedIO.LoadBenchmark.

The candidate is the current checkout. The baseline core is built from a pinned
git revision (default: the main commit PR #182 last merged) and swapped into a
copy of the same runner, so both engines run identical harness code. Everything
is written under TestResults/load-benchmark with a manifest of revisions and
SHA-256 hashes.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import shutil
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "test/EmbedIO.LoadBenchmark/EmbedIO.LoadBenchmark.csproj"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--baseline", default="1445c238afea7d5a237088e8f19c7a4e84eb43f1")
parser.add_argument("--output", type=Path, default=ROOT / "TestResults/load-benchmark")
args = parser.parse_args()
out = args.output.resolve()
out.relative_to((ROOT / "TestResults").resolve())
out.mkdir(parents=True, exist_ok=True)


def run(command, cwd, log):
    with (out / log).open("w", encoding="utf-8") as stream:
        subprocess.run(command, cwd=cwd, stdout=stream, stderr=subprocess.STDOUT, check=True)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


candidate_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
dirty = subprocess.check_output(["git", "status", "--porcelain", "--", "src", "test/EmbedIO.LoadBenchmark"], cwd=ROOT, text=True).strip()
baseline_commit = subprocess.check_output(["git", "rev-parse", args.baseline], cwd=ROOT, text=True).strip()

run(["dotnet", "restore", str(PROJECT), "--locked-mode"], ROOT, "candidate-restore.log")
run(["dotnet", "build", str(PROJECT), "-c", "Release", "--no-restore", "-p:ContinuousIntegrationBuild=true"], ROOT, "candidate-build.log")

# The baseline source is exported from git, never from the working tree.
source = out / "baseline-src"
shutil.rmtree(source, ignore_errors=True)
archive = subprocess.check_output(["git", "archive", "--format=tar", baseline_commit, "src", "Directory.Build.props",
                                   "Directory.Packages.props", "global.json", ".editorconfig", "images", "README.md"], cwd=ROOT)
with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
    tar.extractall(source, filter="data")
core = source / "src/EmbedIO/EmbedIO.csproj"
run(["dotnet", "restore", str(core), "--locked-mode"], source, "baseline-restore.log")
run(["dotnet", "build", str(core), "-c", "Release", "-f", "net10.0", "--no-restore", "-p:ContinuousIntegrationBuild=true"], source, "baseline-build.log")

runners = out / "runners"
shutil.rmtree(runners, ignore_errors=True)
built = ROOT / "test/EmbedIO.LoadBenchmark/bin/Release/net10.0"
shutil.copytree(built, runners / "candidate")
shutil.copytree(built, runners / "baseline")
shutil.copy2(source / "src/EmbedIO/bin/Release/net10.0/EmbedIO.dll", runners / "baseline/EmbedIO.dll")

manifest = {
    "candidate_commit": candidate_commit,
    "candidate_uncommitted_changes": dirty.splitlines(),
    "baseline_commit": baseline_commit,
    "runner_sha256": sha256(runners / "candidate/EmbedIO.LoadBenchmark.dll"),
    "candidate_embedio_sha256": sha256(runners / "candidate/EmbedIO.dll"),
    "baseline_embedio_sha256": sha256(runners / "baseline/EmbedIO.dll"),
}
(out / "runners.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(json.dumps(manifest, indent=2))
print("Run, for example:")
print(f"dotnet {runners / 'candidate/EmbedIO.LoadBenchmark.dll'} run --output {out / 'results-<label>'} "
      f"--baseline-dir {runners / 'baseline'} --candidate-revision {candidate_commit} --baseline-revision {baseline_commit} "
      "--server-cpus 0-7 --client-cpus 8-15")
