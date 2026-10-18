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


# Owner-authorized 2026-10-18: informational suggestions for APIs missing from
# netstandard2.0. Match the exact file, diagnostic, reason AND following member.
# Errors/warnings and other diagnostics retain the existing rejection policy.
COMPATIBILITY_SUPPRESSIONS = (
    ("src/EmbedIO/Tus/TusUploadOptions.cs", "Maintainability", "CA1512:Use ArgumentOutOfRangeException throw helper",
     "The shared implementation targets netstandard2.0, which has no ArgumentOutOfRangeException throw helpers.",
     r"public\s+TusUploadOptions\s*\("),
    ("src/EmbedIO/Tus/DiskTusUploadStore.cs", "Maintainability", "CA1510:Use ArgumentNullException throw helper",
     "The shared implementation targets netstandard2.0, which has no ArgumentNullException.ThrowIfNull.",
     r"public\s+async\s+Task\s+CopyCompletedToAsync\s*\("),
    ("src/EmbedIO/Tus/DiskTusUploadStore.cs", "Performance", "CA1835:Prefer the memory-based overloads for ReadAsync and WriteAsync",
     "The shared implementation targets netstandard2.0, whose Stream API provides the byte-array async overloads.",
     r"internal\s+async\s+Task<\(TusUpload\s+Upload,\s*bool\s+BecameComplete\)>\s+PatchAsync\s*\("),
    ("src/EmbedIO/Tus/DiskTusUploadStore.cs", "Maintainability", "CA1513:Use ObjectDisposedException throw helper",
     "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf.",
     r"private\s+void\s+EnsureOpen\s*\("),
    ("src/EmbedIO/Tus/DiskTusUploadStore.Persistence.cs", "Performance", "CA1850:Prefer static HashData method over ComputeHash",
     "The shared implementation targets netstandard2.0, which has no SHA256.HashData.",
     r"private\s+static\s+string\s+OwnerHash\s*\("),
    ("src/EmbedIO/Tus/TusUploadModule.cs", "Performance", "CA1835:Prefer the memory-based overloads for ReadAsync and WriteAsync",
     "The shared implementation targets netstandard2.0, whose Stream API provides the byte-array async overloads.",
     r"private\s+static\s+async\s+Task\s+RequireEmptyBody\s*\("),
    ("src/EmbedIO/Security/BrowserRequestProtection.cs", "Maintainability", "CA1513:Use ObjectDisposedException throw helper",
     "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf.",
     r"private\s+void\s+EnsureOpen\s*\("),
    ("src/EmbedIO/Security/BrowserRequestProtection.cs", "Style", "IDE0330:Use 'System.Threading.Lock'",
     "The shared implementation targets netstandard2.0, which has no System.Threading.Lock.",
     r"private\s+readonly\s+object\s+_sync\s*="),
    ("src/EmbedIO/Tus/DiskTusUploadStore.cs", "Style", "IDE0330:Use 'System.Threading.Lock'",
     "The shared implementation targets netstandard2.0, which has no System.Threading.Lock.",
     r"private\s+readonly\s+object\s+_sync\s*="),
    ("src/EmbedIO/Tus/TusUploadModule.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"protected\s+override\s+async\s+Task\s+OnRequestAsync\s*\("),
    ("src/EmbedIO/Tus/TusUploadModule.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"internal\s+static\s+void\s+ValidateMetadata\s*\("),
)
COMPATIBILITY_ATTRIBUTE = re.compile(
    r'\[System\.Diagnostics\.CodeAnalysis\.SuppressMessage\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,'
    r'\s*Justification\s*=\s*"([^"]+)"\s*\)\]')


def compatible_attribute_lines(text, filename):
    """Permit only the exact informational compatibility attributes above."""
    allowed = set()
    for match in COMPATIBILITY_ATTRIBUTE.finditer(text):
        for path, category, diagnostic, reason, member in COMPATIBILITY_SUPPRESSIONS:
            if (filename == path and match.groups() == (category, diagnostic, reason)
                    and re.match(r"\s*" + member, text[match.end():])):
                allowed.update(range(text.count("\n", 0, match.start()) + 1,
                                     text.count("\n", 0, match.end()) + 2))
    return allowed


def violations(text, csharp=False, filename=None):
    """Return source lines that explicitly disable or exempt diagnostics."""
    compatible_lines = compatible_attribute_lines(text, filename) if csharp else set()
    failures = [
        (number, line.strip())
        for number, line in enumerate(text.splitlines(), 1)
        if any(pattern.search(line) for pattern in PATTERNS)
        and number not in compatible_lines
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
            failures.append(f"{name}:{number}: {text}")
    if failures:
        print("Unapproved compiler/analyzer suppressions are not permitted:", file=sys.stderr)
        print("\n".join(failures), file=sys.stderr)
        return 1
    print("No unapproved compiler/analyzer suppressions or build opt-outs found.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
