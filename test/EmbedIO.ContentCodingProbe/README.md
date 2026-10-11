# Content-coding format probe

This test-only executable is outside the solution and production packages. It records
BCL raw DEFLATE and zlib decoding behavior, including a valid raw stored-block body
whose first two bytes also form a common zlib header. It writes a small binary
corpus and JSON observations to the supplied output directory. Matching-format
round trips are asserted; malformed-input results are observations, not acceptance
criteria for the HTTP engine.

Run from the repository root:

```powershell
dotnet restore test/EmbedIO.ContentCodingProbe/EmbedIO.ContentCodingProbe.csproj --locked-mode
dotnet run --project test/EmbedIO.ContentCodingProbe/EmbedIO.ContentCodingProbe.csproj -c Release --no-restore -- TestResults/http-engine/content-coding-probe
```

Create the output directory first. The October 9, 2026 Windows .NET 10.0.12 run
showed raw and zlib are different formats. The constructed `78 9C` raw body
successfully produces 156 `A` bytes with `DeflateStream`, and fails with
`ZLibStream`. Two-byte sniffing therefore cannot preserve every legacy raw body.
The zlib decoder returned the complete expected body for missing or partial
checksums and a trailing byte, while rejecting a corrupt checksum. These findings
require explicit format migration and stronger completion validation; they do
not establish cross-platform behavior or HTTP engine conformance.

HTTP's `deflate` coding requires a zlib envelope:
[RFC 9110 section 8.4.1.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.4.1.2).
The stored-block padding and length construction follows
[RFC 1951 section 3.2.4](https://www.rfc-editor.org/rfc/rfc1951.html#section-3.2.4).
