"""Reject repository-owned compiler/analyzer opt-outs in source and build files."""

from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
SOURCE_SUFFIXES = {".cs", ".csproj", ".props", ".targets", ".yml", ".yaml", ".ps1"}
PATTERNS = (
    re.compile(r"^\s*#pragma\s+warning\s+(?:disable|restore)\b", re.IGNORECASE),
    re.compile(r"\b(?:Global)?SuppressMessage(?:Attribute)?\s*\("),
    re.compile(r"^\s*#nullable\s+disable\b", re.IGNORECASE),
    re.compile(r"\.severity\s*=\s*(?:none|silent)\b", re.IGNORECASE),
    re.compile(r"<(?:NoWarn|WarningsNotAsErrors)\b", re.IGNORECASE),
    re.compile(r"(?:RunAnalyzers(?:DuringBuild)?|EnableNETAnalyzers|EnforceCodeStyleInBuild|TreatWarningsAsErrors)\s*(?:=|>)\s*(?:[\"']\s*)?false\b", re.IGNORECASE),
    re.compile(r"<WarningLevel>\s*0\s*</WarningLevel>", re.IGNORECASE),
    re.compile(r"<Nullable>\s*disable\s*</Nullable>", re.IGNORECASE),
    re.compile(r"<AnalysisMode>\s*(?:none|disabled)\s*</AnalysisMode>", re.IGNORECASE),
)

# Mask literals/comments before recognizing a postfix nullable-suppression token.
CS_LITERALS = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'')
NULL_FORGIVING = re.compile(r'(?P<previous>\b\w+|[\]\)])\s*!(?!=)')


def violations(text, csharp=False):
    """Return source lines that explicitly disable or exempt diagnostics."""
    failures = [
        (number, line.strip())
        for number, line in enumerate(text.splitlines(), 1)
        if any(pattern.search(line) for pattern in PATTERNS)
    ]
    if csharp:
        code = CS_LITERALS.sub(lambda match: re.sub(r'[^\n]', ' ', match.group()), text)
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
        for number, text in violations(path.read_text(encoding="utf-8-sig"), csharp=path.suffix == ".cs"):
            failures.append(f"{name}:{number}: {text}")
    if failures:
        print("Compiler/analyzer suppressions are not permitted:", file=sys.stderr)
        print("\n".join(failures), file=sys.stderr)
        return 1
    print("No compiler/analyzer suppression directives or build opt-outs found.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
