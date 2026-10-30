"""Read-only source formatting and informational-diagnostic CI gates."""
from __future__ import annotations

import argparse
import difflib
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
VERSION = "1.1.10"
DIAGNOSTIC = re.compile(
    r"^[ \t]*(?:.+?:[ \t]*)?(info|warning|error)[ \t]+[^\s:]+:", re.MULTILINE)
LOAD_FAILURE = re.compile(
    r"Required references did not load|Exception encountered|Failed to load|Unable to load|"
    r"error MSB\d+|error NETSDK\d+|Unhandled exception", re.IGNORECASE)


def expected_format(path: str, formatted: str) -> str:
    """Exact workarounds for pinned formatter bugs; never exempt an entire file."""
    if path == "src/EmbedIO/WebSockets/Internal/WebSocketFrameStream.cs":
        formatted = formatted.replace("i* ExactAllocationLimit", "i * ExactAllocationLimit")
    if path == "src/EmbedIO/Caching/OutputCacheCapture.cs":
        for reason in (
            "The handler owns transport completion; capture disposal releases only the recording buffer via ReleaseCapture.",
            "Borrowed parent reference; disposing it here would discard the buffer before cache publication.",
        ):
            pattern = (r'\[System\.Diagnostics\.CodeAnalysis\.SuppressMessage\(\s*"Usage",\s*'
                       r'"CA2213:Disposable fields should be disposed",\s*Justification = "'
                       + re.escape(reason) + r'"\s*\)\]')
            replacement = ('[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", '
                           '"CA2213:Disposable fields should be disposed", Justification = "' + reason + '")]')
            formatted = re.sub(pattern, lambda _: replacement, formatted)
    return formatted


def scan_failed(returncode: int, output: str) -> bool:
    # dotnet format returns 2 for proposed fixes, including hidden hints. Do not
    # promote hidden hints, but do fail incomplete loads even when exit code is 0.
    return (returncode not in (0, 2) or "Format complete in " not in output
            or bool(LOAD_FAILURE.search(output)) or bool(DIAGNOSTIC.search(output)))


def check_pretty(report: Path) -> bool:
    import prettycsharp
    from prettycsharp.config import settings_for
    if prettycsharp.__version__ != VERSION:
        raise RuntimeError(f"Install pinned prettycsharp {VERSION}; found {prettycsharp.__version__}")
    differences = []
    files = sorted(p for p in (ROOT / "src").rglob("*.cs")
                   if not {"bin", "obj"}.intersection(p.relative_to(ROOT).parts))
    if not files:
        raise RuntimeError("No source files discovered")
    for path in files:
        relative = path.relative_to(ROOT).as_posix()
        source = path.read_text(encoding="utf-8")
        settings = settings_for(path)
        try:
            formatted = prettycsharp.format_source(source.lstrip("\ufeff"), settings, lang_version=14.0)
        except Exception as error:
            raise RuntimeError(f"{relative}: prettyCSharp failed: {error}") from error
        expected = expected_format(relative, formatted).replace("\r\n", "\n")
        if settings.charset == "utf-8-bom" or (settings.charset == "preserve" and source.startswith("\ufeff")):
            expected = "\ufeff" + expected
        if source != expected:
            print(f"{relative}: prettyCSharp formatting differs", file=sys.stderr)
            differences.extend(difflib.unified_diff(source.splitlines(True), expected.splitlines(True),
                                                   fromfile=relative, tofile=relative + " (formatted)"))
    (report / "pretty.diff").write_text("".join(differences), encoding="utf-8")
    print(f"prettyCSharp: checked {len(files)} source files")
    return not differences


def check_messages(report: Path, selected=None, kinds=("style", "analyzers")) -> bool:
    projects = sorted((ROOT / "src").rglob("*.csproj"))
    if selected:
        requested = {(ROOT / path).resolve() for path in selected}
        available = {project.resolve() for project in projects}
        if not requested <= available:
            raise ValueError(f"Unknown source projects: {requested - available}")
        projects = [project for project in projects if project.resolve() in requested]
    if not projects:
        raise RuntimeError("No source projects discovered")
    passed = True
    summary = []
    for project in projects:
        for kind in kinds:
            name = f"{project.stem}-{kind}"
            command = ["dotnet", "format", kind, str(project), "--no-restore", "--severity", "hidden",
                       "--verify-no-changes", "--report", str(report / f"{name}.json"), "--verbosity", "normal"]
            result = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", timeout=240)
            (report / f"{name}.log").write_text(result.stdout, encoding="utf-8")
            failed = scan_failed(result.returncode, result.stdout)
            diagnostics = [line for line in result.stdout.splitlines() if DIAGNOSTIC.match(line)]
            for line in diagnostics:
                print(line, file=sys.stderr)
            print(f"{name}: {'FAIL' if failed else 'PASS'}", flush=True)
            summary.append({"project": project.relative_to(ROOT).as_posix(), "kind": kind,
                            "exitCode": result.returncode, "passed": not failed, "diagnostics": diagnostics})
            passed = passed and not failed
    (report / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return passed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("pretty", "messages", "all"), default="all")
    parser.add_argument("--project", action="append", help="Explicit source project shard; omit to scan every source project")
    parser.add_argument("--kind", action="append", choices=("style", "analyzers"), help="Explicit diagnostic shard; omit to scan both kinds")
    parser.add_argument("--report-dir", type=Path, help="Separate evidence directory for a bounded shard")
    args = parser.parse_args()
    report = args.report_dir or ROOT / "TestResults" / "source-quality"
    report.mkdir(parents=True, exist_ok=True)
    passed = True
    if args.mode in ("pretty", "all"):
        passed = check_pretty(report)
    if args.mode in ("messages", "all"):
        passed = check_messages(report, args.project, args.kind or ("style", "analyzers")) and passed
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
