"""Reject repository-owned compiler/analyzer opt-outs in source and build files."""

from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
SOURCE_SUFFIXES = {".cs", ".csproj", ".props", ".targets", ".yml", ".yaml", ".ps1"}
PATTERNS = (
    re.compile(r"^\s*#pragma\s+warning\s+(?:disable|restore)\b", re.IGNORECASE),
    re.compile(r"\b(?:(?:Global|Unconditional)?SuppressMessage)(?:Attribute)?\s*\("),
    re.compile(r"^\s*#nullable\s+disable\b", re.IGNORECASE),
    re.compile(r"\.severity\s*=\s*(?:none|silent)\b", re.IGNORECASE),
    re.compile(r"<(?:NoWarn|WarningsNotAsErrors)\b", re.IGNORECASE),
    re.compile(r"\b(?:NoWarn|WarningsNotAsErrors)\s*=|[/\-]nowarn\s*:", re.IGNORECASE),
    re.compile(r"(?:RunAnalyzers(?:DuringBuild|DuringLiveAnalysis)?|EnableNETAnalyzers|EnforceCodeStyleInBuild|TreatWarningsAsErrors)\s*(?:=|>)\s*(?:[\"']\s*)?false\b", re.IGNORECASE),
    re.compile(r"<WarningLevel>\s*0\s*</WarningLevel>", re.IGNORECASE),
    re.compile(r"<Nullable>\s*disable\s*</Nullable>", re.IGNORECASE),
    re.compile(r"(?:AnalysisMode|AnalysisLevel)\s*(?:=|>)\s*(?:none|disabled)\b", re.IGNORECASE),
)

# Mask literals/comments before recognizing a postfix nullable-suppression token.
CS_LITERALS = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'')
NULL_FORGIVING = re.compile(r'(?P<previous>\b\w+|[\]\)])\s*!(?!=)')


def violations(text, csharp=False, filename=None):
    """Return source lines that explicitly disable or exempt diagnostics."""
    failures = [
        (number, line.strip())
        for number, line in enumerate(text.splitlines(), 1)
        if any(pattern.search(line) for pattern in PATTERNS)
        # Owner-authorized ownership suppressions: capture release must not close its borrowed transport.
        and not (filename == "src/EmbedIO/Caching/OutputCacheCapture.cs"
                 and line.strip() in {
                     '[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The handler owns transport completion; capture disposal releases only the recording buffer via ReleaseCapture.")]',
                     '[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Borrowed parent reference; disposing it here would discard the buffer before cache publication.")]',
                 })
        # Owner-approved folder/namespace convention.
        and not (filename == ".editorconfig"
                 and line.strip() == "dotnet_diagnostic.IDE0130.severity = none")
    ]
    if csharp:
        code = CS_LITERALS.sub(lambda match: re.sub(r'[^\n]', ' ', match.group()), text)
        # Conditional directive expressions use logical negation, never nullable suppression.
        code = re.sub(r"(?m)^\s*#(?:if|elif)\b[^\n]*",
                      lambda match: re.sub(r'[^\n]', ' ', match.group()), code)
        for match in NULL_FORGIVING.finditer(code):
            if match.group('previous') in {'return', 'throw', 'await', 'yield', 'case'}:
                continue
            number = code.count('\n', 0, match.start()) + 1
            failures.append((number, text.splitlines()[number - 1].strip()))
    return sorted(set(failures))


def main():
    files = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=ROOT,
    ).decode("utf-8").split("\0")
    failures = []
    for name in sorted(set(files)):
        path = ROOT / name
        if not path.is_file() or (path.suffix not in SOURCE_SUFFIXES and path.name not in {".editorconfig", ".globalconfig"}):
            continue
        for number, text in violations(path.read_text(encoding="utf-8-sig"), csharp=path.suffix == ".cs", filename=name):
            # Owner-approved namespace and compatibility policies, limited to the root configuration.
            if name == ".editorconfig" and text in {
                "dotnet_diagnostic.CA1870.severity = none",
                "dotnet_diagnostic.CA1861.severity = none",
                "dotnet_diagnostic.IDE0300.severity = none",
                "dotnet_diagnostic.IDE0060.severity = none",
                "dotnet_diagnostic.CA1307.severity = none",
                "dotnet_diagnostic.CA2208.severity = none",
                "dotnet_diagnostic.CA1510.severity = none",
                "dotnet_diagnostic.CA1513.severity = none",
                "dotnet_diagnostic.CA1512.severity = none",
                "dotnet_diagnostic.CA1835.severity = none",
                "dotnet_diagnostic.CA1846.severity = none",
                "dotnet_diagnostic.CA1865.severity = none",
                "dotnet_diagnostic.CA1866.severity = none",
                "dotnet_diagnostic.IDE0057.severity = none",
                "dotnet_diagnostic.IDE0056.severity = none",
                "dotnet_diagnostic.IDE0130.severity = none",
                "dotnet_diagnostic.IDE0330.severity = none",
                "dotnet_diagnostic.IDE0230.severity = none",
                "dotnet_diagnostic.SYSLIB1045.severity = none",
            }:
                continue
            failures.append(f"{name}:{number}: {text}")
    if failures:
        print("Compiler/analyzer suppressions are not permitted:", file=sys.stderr)
        print("\n".join(failures), file=sys.stderr)
        return 1
    print("No compiler/analyzer suppression directives or build opt-outs found.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
