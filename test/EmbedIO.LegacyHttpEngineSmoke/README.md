# Legacy managed HTTP engine smoke

Test-only executable targeting .NET Framework 4.7.2 and loading the core's
.NET Standard 2.0 asset. It remains outside the solution and shipped packages.
The Windows compatibility CI job builds and runs it on the installed Framework;
the report records the actual registry release, CLR, loaded target, source version
and engine SHA-256. Targeting 4.7.2 does not establish execution on exactly 4.7.2.

The fixture uses only public EmbedIO APIs and independent wire input. It verifies:

- 256 HTTP/1 requests retain one connection beyond the removed 100-request cap.
- A 65,537-byte chunked upload returns every original byte.
- Conflicting Content-Length and Transfer-Encoding are rejected with 400 and EOF.
- A prior-knowledge HTTP/2 request receives HEADERS and the complete expected DATA,
  without GOAWAY or a reset, while respecting the default frame-size limit.
- Listener cancellation completes within ten seconds.

Run from the repository root on Windows:

```powershell
dotnet restore test/EmbedIO.LegacyHttpEngineSmoke/EmbedIO.LegacyHttpEngineSmoke.csproj --locked-mode
dotnet build test/EmbedIO.LegacyHttpEngineSmoke/EmbedIO.LegacyHttpEngineSmoke.csproj -c Release --no-restore
& test/EmbedIO.LegacyHttpEngineSmoke/bin/Release/net472/EmbedIO.LegacyHttpEngineSmoke.exe
```

Evidence is written to ignored `TestResults/legacy-http-engine.json`. No firewall,
URL ACL or trust configuration is changed. This is a focused runtime smoke, not
full HTTP/2 conformance, HPACK decoder validation, TLS/ALPN coverage or HTTP/3
support on .NET Framework. Native QUIC remains subject to documented platform and
target limits.