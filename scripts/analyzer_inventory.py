"""Build every repository project with analyzers forced on and count diagnostics.

Warnings are errors in this repository, so a passing build is a zero-warning
build. The solution gate in CI covers EmbedIO.sln; this inventory also builds
each project outside the solution so the baseline recorded under
TestResults/analyzer-inventory describes the whole repository. Projects whose
target framework needs a platform workload (Android, iOS, Mac Catalyst, Windows
App SDK) are listed as skipped rather than silently omitted; their CI jobs build
them. Logs stay under the ignored TestResults folder.
"""
import argparse
from pathlib import Path
import re
import subprocess
import sys

DIAGNOSTIC = re.compile(r": (?P<kind>warning|error) (?P<code>[A-Z]+\d+):")
WORKLOAD_TARGET = re.compile(r"<TargetFramework[^>]*>\s*net\d+\.\d+-(?:android|ios|maccatalyst|tvos|windows)", re.IGNORECASE)
ANALYZER_FLAGS = ("-c", "Release", "-p:RunAnalyzers=true", "-p:EnforceCodeStyleInBuild=true", "--no-restore")


def count_diagnostics(log_text):
    """Return (warnings, errors) as counts of distinct diagnostic lines.

    MSBuild repeats a diagnostic for every project that compiles the same file,
    so identical lines are counted once.
    """
    warnings, errors = set(), set()
    for line in log_text.splitlines():
        match = DIAGNOSTIC.search(line)
        if match is None:
            continue
        (warnings if match.group("kind") == "warning" else errors).add(line.strip())
    return len(warnings), len(errors)


def solution_members(listing):
    """Parse `dotnet sln list` output into normalized relative project paths."""
    members = set()
    for line in listing.splitlines():
        text = line.strip()
        if not text.lower().endswith(".csproj"):
            continue
        members.add(text.replace("\\", "/"))
    return members


def requires_workload(csproj_text):
    """True when a project targets a platform framework that needs a workload."""
    return WORKLOAD_TARGET.search(csproj_text) is not None


def summary_row(label, restore, build, warnings, errors):
    return f"{label}\t{restore}\t{build}\t{warnings}\t{errors}"


def run(command, cwd, log_path):
    completed = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    log_path.write_text(completed.stdout + completed.stderr, encoding="utf-8")
    return completed.returncode, completed.stdout + completed.stderr


def inventory(root, out):
    out.mkdir(parents=True, exist_ok=True)
    rows = ["project\trestore\tbuild\twarnings\terrors"]
    failures = 0

    def build(label, target, restore_kind):
        nonlocal failures
        code, text = run(["dotnet", "build", target, *ANALYZER_FLAGS], root, out / f"{label}.log")
        warnings, errors = count_diagnostics(text)
        status = "ok" if code == 0 and "Build succeeded" in text else "FAILED"
        if status != "ok" or warnings:
            failures += 1
        rows.append(summary_row(label, restore_kind, status, warnings, errors))

    code, _ = run(["dotnet", "restore", "EmbedIO.sln", "--locked-mode"], root, out / "solution.restore.log")
    build("EmbedIO.sln", "EmbedIO.sln", "locked" if code == 0 else "locked-FAILED")

    code, listing = run(["dotnet", "sln", "EmbedIO.sln", "list"], root, out / "solution.list.log")
    members = solution_members(listing) if code == 0 else set()

    for csproj in sorted(root.glob("**/*.csproj")):
        relative = csproj.relative_to(root).as_posix()
        if relative.startswith("TestResults/") or "/bin/" in relative or "/obj/" in relative:
            continue
        if relative in members:
            continue
        label = relative.replace("/", "_")[: -len(".csproj")]
        if requires_workload(csproj.read_text(encoding="utf-8")):
            rows.append(summary_row(label, "n/a", "skipped-workload", "-", "-"))
            continue
        locked = any(csproj.parent.glob("packages.lock.json"))
        restore_command = ["dotnet", "restore", relative] + (["--locked-mode"] if locked else [])
        code, _ = run(restore_command, root, out / f"{label}.restore.log")
        restore_kind = ("locked" if locked else "unlocked") + ("" if code == 0 else "-FAILED")
        build(label, relative, restore_kind)

    (out / "summary.tsv").write_text("\n".join(rows) + "\n", encoding="utf-8")
    return rows, failures


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(Path(__file__).resolve().parents[1]))
    parser.add_argument("--out", default=None, help="defaults to <root>/TestResults/analyzer-inventory")
    args = parser.parse_args(argv)
    root = Path(args.root).resolve()
    out = Path(args.out).resolve() if args.out else root / "TestResults" / "analyzer-inventory"
    rows, failures = inventory(root, out)
    widths = [max(len(row.split("\t")[column]) for row in rows) for column in range(5)]
    for row in rows:
        print("  ".join(cell.ljust(widths[index]) for index, cell in enumerate(row.split("\t"))))
    print(f"logs: {out}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
