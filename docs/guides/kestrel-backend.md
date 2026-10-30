# Optional Kestrel backend

This backend is pending development, with no released package yet. The built-in
EmbedIO engine remains the default. `EmbedIO-Neo.Kestrel` supplies an explicit
.NET 10 alternative while keeping `WebServer`, modules, controllers, routing,
sessions and the ordinary request/response interfaces.

```csharp
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Kestrel;

var options = new WebServerOptions()
    .WithUrlPrefix("http://localhost:9696/")
    .WithKestrel();
using var server = new WebServer(options);
server.WithModule(new ActionModule(context =>
    context.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding)));
await server.RunAsync(cancellationToken);
```

Add the optional package at the same version as `EmbedIO-Neo`. Existing code that
does not call `WithKestrel()` keeps its existing engine and dependencies.
`WebServerOptions.ListenerFactory` is the locked configuration hook used by the
extension; configure it before constructing the server.

## Protocols and deployment

| Hosting mode | Kestrel transport |
| --- | --- |
| `EmbedIO` | HTTP/1.1 and HTTP/2 on each TCP port. Cleartext uses the HTTP/2 prior-knowledge preface; HTTPS uses ALPN. |
| `EmbedIOCombined` | HTTP/1.1, HTTP/2 and HTTP/3 on the same HTTPS port number, over TCP and UDP. |
| `EmbedIOHttp3` | HTTP/3 over QUIC/UDP only. |

HTTPS still requires a certificate with its private key. Select the same
certificate, TLS policy and mode through the existing options. HTTP/3 uses
official Kestrel's `System.Net.Quic`/MsQuic transport; the default engine continues
to use `EmbedIO.Quic`. `AdvertiseHttp3` controls combined-mode advertisement.

No installed `Microsoft.AspNetCore.App` shared runtime is required. The package
bundles unmodified official Kestrel and supporting assemblies, currently pinned
to 10.0.13. An ordinary framework-dependent app needs the base .NET 10 runtime;
a self-contained publish also bundles that runtime. This does not remove the
ASP.NET Core component code from Kestrel.

The initial package includes win-x64, win-arm64, linux-x64, linux-arm64, osx-x64
and osx-arm64 assets. Windows x64 has local runtime evidence; the other RIDs still
need platform execution. HTTP/3 additionally requires a supported System.Net.Quic
platform and native MsQuic, including the appropriate `libmsquic` installation on
Linux. Packaging managed assets does not establish HTTP/3 support on every OS.
See Microsoft's [QUIC platform requirements](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview).
Trimming, Native AOT and strongly signed custom core builds have not been validated
for this optional backend; the built-in engine's support is unchanged.

## Compatibility boundary

The adapter owns a fresh context, request metadata and response for every request.
Retained objects cannot follow Kestrel's pooled features into another request.
An outstanding body read is canceled and settled before Kestrel reuses a request.
Headers, cookies, core admission and body policies, completion callbacks, streaming
flushes, request-abort tokens and WebSocket modules pass through the existing
EmbedIO application pipeline.

Write payload bytes to the response stream and choose framing through
`ContentLength64` or `SendChunked`. Kestrel adds the transport framing.

Applications must check optional capabilities before using them. This backend
currently has these differences:

- It owns its bound ports; independent `WebServer` instances cannot share a port
  through different URL prefixes. Combine modules in one server instead.
- Raw CONNECT tunnels, HTTP/3 application datagrams and `IHttpResponseSections`
  are not exposed. Ordinary
  request trailers are available through `IHttpRequestTrailers` after validated
  input completion, including on retained requests.
- `GetRequestAbortedToken(true)` cannot observe HTTP/1 input half-close and throws
  `NotSupportedException` there. The flag remains ignored for HTTP/2 and HTTP/3.
- Configured WebTransport and forced stateless HTTP/3 address validation fail
  at startup; they are never silently ignored. Use the built-in engine for them.
- Kestrel owns HTTP parsing, framing, HTTP/2 and HTTP/3 scheduling and wire errors.
  Do not depend on byte-identical error pages or transport-specific close codes.

These differences mean the backend is not yet a universal drop-in replacement
for every optional built-in capability. Validate an application's used surface
before selecting it. No performance improvement is claimed from the protocol
checks alone.

## Validation and component servicing

`test/EmbedIO.Kestrel.Compatibility` exercises application surfaces using
`kestrel` or `embedio`: basic, advanced, lifecycle, protocol, TLS and startup groups are separate
runs. Each compatibility group has a short internal deadline; its `run.py`
wrapper also enforces a 50-second execution and five-second cleanup budget.
Windows x64 validation on .NET 10.0.13 completed on 2026-10-30 (Pacific):

| Check | Completed evidence |
| --- | --- |
| Optional backend | 97 checks across all six groups, including shared-port protocols, request ownership, admission, body limits/timeouts, streaming, controllers, sessions and WebSockets. Explicit chunked output covers a small response, a 1 MiB flushed response and its pipelined successor; protocol checks mix fixed-length and chunked responses. |
| TLS policy | Mutual TLS, certificate rotation and missing-client-certificate refusal for HTTP/1.1, HTTP/2 and HTTP/3. |
| Deployment | Fresh NuGet consumer passes 50 protocol checks under an isolated dotnet root containing only `Microsoft.NETCore.App`; a self-contained Windows publish passes the same checks. |
| Default engine | Full documented regression command succeeds: 7,591 total, 7,588 passed and three expected Windows skips. The basic comparison group passes 15 checks. |
| Source and allocation gates | All 597 source files, all nine source projects, changed benchmark/compatibility files, whitespace and analyzer guards pass; existing hot-path, audit, queue, cold-start and listener allocation budgets pass. |
| Package integrity | 283 bundled DLLs, six RID asset groups and upstream notices match the seven pinned official archives. |

The comparison runner is not a claim of identical behavior everywhere. For example,
the built-in response keeps `ContentType` separate from raw response fields, while
Kestrel preserves a raw `Content-Type` assignment. Abort detection remains best
effort. Transport cleanup can finish after peer EOF; lifecycle checks require a
healthy successor within a bounded deadline rather than assuming synchronous cleanup.

Linux/macOS execution, hosted checks and performance comparisons remain pending.
Initial warm attempts were rejected before load because host CPU activity exceeded
the quiet threshold. William subsequently authorized measurements without host idle;
these retain CPU limits, shared-slot ownership and recorded host activity. The first
such attempt exposed explicit chunked framing during warmup and was stopped. The
adapter now delegates that framing to Kestrel, with regression checks above. None
of these attempts establishes a performance improvement. No optional backend QUIC
fuzz campaign has been completed.

`components.json` records the managed closure. `upstream-packs.json` pins SHA-256
of the seven official source package archives. Build and pack verify those pins;
`scripts/generate_kestrel_assets.py` checks the explicit asset map.
`scripts/check_kestrel_package.py` compares every bundled runtime assembly and
upstream license/notice byte against those archives. Service the optional package
when Kestrel receives fixes: update its component version, review the closure,
refresh and review archive hashes, regenerate the map, and repeat package and
runtime validation. The application's installed ASP.NET Core runtime does not
service these private bundled components.

## Disk footprint

The current Windows x64 runtime group adds approximately **7.95 MiB**, including
the adapter and its 46 official components. The .NET 10 `EmbedIO.dll` is about
**1.82 MiB**, giving about **9.76 MiB** of engine DLLs together. These figures
exclude the base .NET runtime and common dependencies and do not describe RAM.

The compressed optional NuGet package is approximately **21.3 MiB** because it
contains six RID groups. Publish for a specific RID, such as
`dotnet publish -c Release -r win-x64 --self-contained false`, to select that
platform's assets. Use `--self-contained true` to include the base runtime too.
Portable deployment can carry several platform groups.
