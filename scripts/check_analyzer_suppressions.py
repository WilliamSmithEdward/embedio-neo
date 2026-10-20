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
    ("src/EmbedIO/WebSockets/WebSocketModule.cs", "Style", "IDE0290:Use primary constructor",
     "Preserve the protected constructor; a primary constructor on this public class would be public.",
     r"protected\s+WebSocketModule\s*\("),
    ("src/EmbedIO/WebSockets/WebSocketMessageModule.cs", "Style", "IDE0028:Simplify collection initialization",
     "The shared implementation targets netstandard2.0, whose ConditionalWeakTable does not support collection expressions.",
     r"private\s+readonly\s+ConditionalWeakTable<IWebSocketContext,\s*DispatchState>\s+_dispatch\s*="),
    ("src/EmbedIO/WebSockets/WebSocketMessageModule.cs", "Style", "IDE0290:Use primary constructor",
     "Preserve the protected constructor; a primary constructor on this public class would be public.",
     r"protected\s+WebSocketMessageModule\s*\("),
    ("src/EmbedIO/WebSockets/Internal/WebSocket.cs", "Performance", "CA1850:Prefer static HashData method over ComputeHash",
     "The shared implementation targets netstandard2.0, which has no SHA1.HashData.",
     r"static\s+string\s+CreateResponseKey\s*\("),
    ("src/EmbedIO/Tunnels/HttpTunnel.cs", "Usage", "CA2219:Do not raise exceptions in finally clauses",
     "Cleanup must always dispose the owned stream and preserve both completion and cleanup failures through aggregation.",
     r"private\s+async\s+Task\s+DisposeCoreAsync\s*\("),
    ("src/EmbedIO/Serialization/CompatibleJsonInput.cs", "Performance", "CA1845:Use span-based string.Concat",
     "The shared implementation targets netstandard2.0, which has no span-based string.Concat overloads.",
     r"private\s+static\s+string\?\s+CanonicalNumber\s*\("),
    ("src/EmbedIO/Internal/Responses/CookieSameSiteStore.cs", "Style", "IDE0028:Simplify collection initialization",
     "The shared implementation targets netstandard2.0, whose ConditionalWeakTable does not support collection expressions.",
     r"private\s+static\s+readonly\s+ConditionalWeakTable<IHttpResponse,\s*Dictionary<Cookie,\s*CookieSameSiteMode>>\s+Values\s*="),
    ("src/EmbedIO/Caching/OutputCacheCapture.cs", "Performance", "CA1844:Override memory-based WriteAsync",
     "The shared implementation targets netstandard2.0, which has no memory-based Stream async overloads.",
     r"private\s+sealed\s+class\s+CaptureStream"),
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
    ("src/EmbedIO/Caching/OutputCache.cs", "Style", "IDE0330:Use 'System.Threading.Lock'",
     "The shared implementation targets netstandard2.0, which has no System.Threading.Lock.",
     r"private\s+readonly\s+object\s+_gate\s*="),
    ("src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs", "Style", "IDE0330:Use 'System.Threading.Lock'",
     "The shared implementation targets netstandard2.0, which has no System.Threading.Lock.",
     r"private\s+readonly\s+object\s+_lifecycle\s*="),
    ("src/EmbedIO/Security/SecurityHeadersState.cs", "Style", "IDE0330:Use 'System.Threading.Lock'",
     "The shared implementation targets netstandard2.0, which has no System.Threading.Lock.",
     r"private\s+readonly\s+object\s+_gate\s*="),
    ("src/EmbedIO/Net/Internal/Http1/WebSocketHandshakeResponse.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"internal\s+WebSocketHandshakeResponse\s*\("),
    ("src/EmbedIO/Caching/OutputCache.cs", "Performance", "CA1835:Prefer memory-based stream operations",
     "The shared implementation targets netstandard2.0, which has no memory-based Stream async overloads.",
     r"internal\s+async\s+Task\s+ReplayAsync\(IHttpContext\s+context\)"),
    ("src/EmbedIO/Caching/OutputCache.cs", "Maintainability", "CA1512:Use ArgumentOutOfRangeException throw helper",
     "The shared implementation targets netstandard2.0, which has no ArgumentOutOfRangeException throw helpers.",
     r"public\s+OutputCache\(\s+int\s+maxEntries\s+=\s+256,\s+long\s+maxBytes\s+=\s+16\s+\*\s+1024\s+\*\s+1024,\s+int\s+maxResponseBytes\s+=\s+256\s+\*\s+1024,\s+int\s+maxConcurrentFills\s+=\s+16,\s+int\s+maxKeyCharacters\s+=\s+8192\s+\)"),
    ("src/EmbedIO/Hosting/WebServerBase.cs", "Maintainability", "CA1513:Use ObjectDisposedException throw helper",
     "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf.",
     r"public\s+async\s+Task\s+RunAsync\(CancellationToken\s+cancellationToken\s+=\s+default\)"),
    ("src/EmbedIO/Hosting/WebServerOptionsBase.cs", "Maintainability", "CA1512:Use ArgumentOutOfRangeException throw helper",
     "The shared implementation targets netstandard2.0, which has no ArgumentOutOfRangeException throw helpers.",
     r"public\s+int\s+RequestQueueLimit"),
    ("src/EmbedIO/Idempotency/IdempotencyCoordinator.cs", "Performance", "CA1850:Prefer static HashData",
     "The shared implementation targets netstandard2.0, which has no SHA256.HashData.",
     r"private\s+static\s+byte\[\]\s+HashBytes\(byte\[\]\s+value\)"),
    ("src/EmbedIO/Idempotency/IdempotencyCoordinator.cs", "Performance", "CA1835:Prefer memory-based stream operations",
     "The shared implementation targets netstandard2.0, which has no memory-based Stream async overloads.",
     r"private\s+static\s+async\s+Task<byte\[\]>\s+ReadBodyAsync\(IHttpContext\s+context,\s+int\s+limit\)"),
    ("src/EmbedIO/Idempotency/IdempotencyCoordinator.cs", "Maintainability", "CA1512:Use ArgumentOutOfRangeException throw helper",
     "The shared implementation targets netstandard2.0, which has no ArgumentOutOfRangeException throw helpers.",
     r"public\s+IdempotencyCoordinator\(IIdempotencyStore\?\s+store\s+=\s+null,\s+int\s+maxConcurrentRequests\s+=\s+32\)"),
    ("src/EmbedIO/Net/Internal/Http1/HttpListenerResponse.cs", "Maintainability", "CA1513:Use ObjectDisposedException throw helper",
     "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf.",
     r"private\s+void\s+RequireMutableHead\(\)"),
    ("src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs", "Performance", "CA1844:Override memory-based WriteAsync",
     "The shared implementation targets netstandard2.0, which has no memory-based Stream.WriteAsync override.",
     r"private\s+sealed\s+class\s+Output"),
    ("src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs", "Maintainability", "CA1513:Use ObjectDisposedException throw helper",
     "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf.",
     r"private\s+async\s+Task\s+WriteAsync\(byte\[\]\s+buffer,\s+int\s+offset,\s+int\s+count,\s+CancellationToken\s+token\)"),
    ("src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs", "Maintainability", "CA1513:Use ObjectDisposedException throw helper",
     "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf.",
     r"private\s+void\s+EnterSectionConfiguration\(\)"),
    ("src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs", "Maintainability", "CA1513:Use ObjectDisposedException throw helper",
     "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf.",
     r"private\s+void\s+EnsureHeaders\(\)"),
    ("src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs", "Maintainability", "CA1512:Use ArgumentOutOfRangeException throw helper",
     "The shared implementation targets netstandard2.0, which has no ArgumentOutOfRangeException throw helpers.",
     r"public\s+long\s+ContentLength64"),
    ("src/EmbedIO/Security/SecurityHeadersPolicy.cs", "Usage", "CA2249:Use string.Contains",
     "The shared implementation targets netstandard2.0, which has no string.Contains overload with StringComparison.",
     r"private\s+static\s+void\s+ValidateCsp\(string\?\s+value\)"),
    ('src/EmbedIO/Http/Results/HttpResults.Files.cs', 'Maintainability', 'CA1513:Use ObjectDisposedException throw helper',
     'The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf.',
     r"internal\s+override\s+async\s+Task\s+WriteAsync\(IHttpContext\s+context,\s*ResponseSerializerCallback\s+serializer\)"),
    ("src/EmbedIO/OpenApi/OpenApiDocumentBuilder.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"private\s+static\s+bool\s+IsJson\(string\s+contentType\)"),
    ("src/EmbedIO/OpenApi/OpenApiDocumentBuilder.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"private\s+static\s+string\s+SchemaName\(Type\s+type\)"),
    ("src/EmbedIO/OpenApi/OpenApiDocumentBuilder.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"private\s+static\s+JsonNode\?\s+RebaseReferences\(JsonNode\?\s+node,\s*string\s+root\)"),
    ("src/EmbedIO/OpenApi/OpenApiDocumentBuilder.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"private\s+static\s+\(string\s+Path,\s*List<\(string\s+Name,\s*bool\s+Optional\)>\s+Parameters\)\s+PathTemplate\(string\s+route\)"),
    ("src/EmbedIO/OpenApi/OpenApiDocumentBuilder.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"private\s+static\s+string\s+TrimSuffix\(string\s+value,\s*string\s+suffix\)"),
    ("src/EmbedIO/Routing/NamedRoutes.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"public\s+string\s+GetPath\(string\s+name,\s*IReadOnlyDictionary<string,\s*object\?>\?\s+values\s*=\s*null\)"),

    ("src/EmbedIO/Routing/NamedRoutes.cs", "Performance", "CA1865:Use char overload",
     "The shared implementation targets netstandard2.0, which has no string.EndsWith(char).",
     r"public\s+string\s+GetPath\(string\s+name,\s*IReadOnlyDictionary<string,\s*object\?>\?\s+values\s*=\s*null\)"),
    ("src/EmbedIO/OpenApi/OpenApiDocumentBuilder.cs", "Performance", "CA1865:Use char overload",
     "The shared implementation targets netstandard2.0, which has no string.EndsWith(char).",
     r"private\s+static\s+\(string\s+Path,\s*List<\(string\s+Name,\s*bool\s+Optional\)>\s+Parameters\)\s+PathTemplate\(string\s+route\)"),
    ("src/EmbedIO/WebApi/BoundedRequestFields.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"internal\s+static\s+NameValueCollection\s+Query\s*\("),
    ("src/EmbedIO/WebApi/BoundedRequestFields.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"internal\s+static\s+NameValueCollection\s+Parse\s*\("),
    ("src/EmbedIO/WebApi/RequestFieldPlan.cs", "Style", "IDE0057:Use range operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"public\s+T\s+Bind\s*\("),
    ("src/EmbedIO/WebApi/RequestFieldPlan.cs", "Style", "IDE0056:Use index operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"public\s+T\s+Bind\s*\("),
    ("src/EmbedIO/WebApi/ReflectionRequestBinding.cs", "Style", "IDE0056:Use index operator",
     "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.",
     r"private\s+static\s+Expression\s+Scalar\s*\("),
    ("src/EmbedIO/WebApi/BoundedRequestFields.cs", "Performance", "CA1835:Prefer the memory-based overloads for ReadAsync and WriteAsync",
     "The shared implementation targets netstandard2.0, whose Stream API provides the byte-array async overloads.",
     r"internal\s+static\s+async\s+Task<NameValueCollection>\s+FormAsync\s*\("),
)
COMPATIBILITY_ATTRIBUTE = re.compile(
    r'\[System\.Diagnostics\.CodeAnalysis\.SuppressMessage\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,'
    r'\s*Justification\s*=\s*"([^"]+)"\s*\)\]')


def compatible_attribute_lines(text, filename):
    """Permit only the exact informational compatibility attributes above."""
    allowed = set()
    for match in COMPATIBILITY_ATTRIBUTE.finditer(text):
        # Stacked attributes must each independently pass the same exact allowlist.
        # Inspect their shared following member regardless of formatter whitespace.
        following = text[match.end():].lstrip()
        while adjacent := COMPATIBILITY_ATTRIBUTE.match(following):
            following = following[adjacent.end():].lstrip()
        for path, category, diagnostic, reason, member in COMPATIBILITY_SUPPRESSIONS:
            if (filename == path and match.groups() == (category, diagnostic, reason)
                    and re.match(r"\s*" + member, following)):
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
            # Owner-approved namespace and compatibility policies, limited to the root configuration.
            if name == ".editorconfig" and text in {
                "dotnet_diagnostic.CA1845.severity = none",
                "dotnet_diagnostic.CA1864.severity = none",
                "dotnet_diagnostic.CA2012.severity = none",
                "dotnet_diagnostic.CA1822.severity = none",
                "dotnet_diagnostic.CA2219.severity = none",
                "dotnet_diagnostic.IDE0290.severity = none",
                "dotnet_diagnostic.CA1844.severity = none",
                "dotnet_diagnostic.CA1068.severity = none",
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
        print("Unapproved compiler/analyzer suppressions are not permitted:", file=sys.stderr)
        print("\n".join(failures), file=sys.stderr)
        return 1
    print("No unapproved compiler/analyzer suppressions or build opt-outs found.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
