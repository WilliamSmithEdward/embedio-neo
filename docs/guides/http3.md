# HTTP/3 listener (unreleased)

The modern engine branch adds `HttpListenerMode.EmbedIOHttp3`. This opt-in mode
serves the normal WebServer modules over QUIC, including request bodies, routing,
responses and sessions. It is under development in PR #182 and is not a released
NuGet feature. The [engine program](../project/http-engine.md) records validation
and the remaining conformance, extension and performance work.

## Requirements

Use the .NET 10 library asset on a host where `QuicListener.IsSupported` is true.
Windows requires usable TLS 1.3 support; Linux requires the native MsQuic library.
CI also exercises macOS with its pinned native prerequisite; this is not a claim
that every macOS deployment/runtime combination is supported. The .NET Standard
asset rejects this mode with `PlatformNotSupportedException`.

On Windows, .NET bundles MsQuic, so use runtime 10.0.13 or later. It is the .NET
security release for [CVE-2026-105016](https://msrc.microsoft.com/update-guide/vulnerability/CVE-2026-105016),
a remote code execution vulnerability in Microsoft QUIC that MSRC rates Critical
for .NET installed on Windows. Runtime 10.0.13 bundles `msquic.dll` 2.5.11;
10.0.12 bundled 2.5.10. Linux and macOS load the MsQuic library installed on the
host, which a runtime update does not change.

Configure HTTPS prefixes and a server certificate containing its private key.
Keep the certificate alive until the server is disposed. The OpenSSL QUIC backend
used in validation requires an exportable key; choose provisioning appropriate to
your host and protect the certificate/password. Use a DNS name matching the
certificate and configure normal client trust. IP-literal URLs worked in the
Windows fixture but failed TLS setup with the tested Linux .NET 10.0.12/MsQuic
2.6.2 client; the portable examples and tests use `localhost`.

Normal Chromium HTTP/3 discovery has an additional certificate-root requirement.
In the isolated Linux Chrome for Testing 156.0.8078.4 check, a locally trusted
private CA passed ordinary HTTPS validation, but QUIC rejected the handshake.
The [exact Chromium source revision](https://chromium.googlesource.com/chromium/src/+/f842f52ef39fd97b449eb27cff414bf05a98ddce/net/quic/crypto/proof_verifier_chromium.cc)
rejects otherwise-valid QUIC chains whose root it does not recognize. Installing
a private CA is therefore insufficient for this normal browser discovery case.
Validation with a publicly trusted certificate remains deferred at the owner's
request; no certificate bypass or forced QUIC was used. This does not describe
.NET or aioquic trust behavior.

HTTP/3 uses UDP. This mode does not bind a TCP listener or accept HTTP/1/HTTP/2.
It does not currently advertise Alt-Svc or configure DNS HTTPS records. Clients
must explicitly select HTTP/3, or discovery must be arranged by the application.
Any network permission must allow the intended UDP endpoint.

## Combined TCP and QUIC hosting

The unreleased `HttpListenerMode.EmbedIOCombined` mode serves HTTP/1.1 and
HTTP/2 over TCP and HTTP/3 over UDP from one `WebServer`, using the same HTTPS
prefixes, certificate and application modules. In the server example below,
replace `WithMode(HttpListenerMode.EmbedIOHttp3)` with
`WithMode(HttpListenerMode.EmbedIOCombined)` to use it.

Both transports are required: startup fails if either binding fails, and rolls
back the bindings owned by that attempt. There is no silent TCP-only fallback.
The .NET Standard asset and hosts without native QUIC reject this mode. Only
HTTPS prefixes are accepted. Allow the intended TCP and UDP endpoints in the
host's network policy. The certificate and client trust requirements above apply.

Each transport has one accept pump feeding a shared queue of at most 256
contexts. A pump can hold one additional context while waiting for queue space;
this does not replace each protocol's connection, stream or input limits.
Canceling one `GetContextAsync` consumer does not cancel the transport pumps.
Stop and disposal abort both transports and wait for the pumps to finish;
after Stop, the listener can be restarted with a fresh session. A transport
accept failure shuts down both transports.

`DrainAsync` coordinates TCP and QUIC with one deadline, including shared TCP
endpoints. Both accept pumps remain active while accepted responses
finish; completion of one transport does not abort the other. Deadline expiry or
cancellation stops queue admission and aborts the remaining responses, including
requests waiting in the bounded queue. Shared TCP endpoints keep accepting for
sibling listeners while refusing new requests for the draining listener. V2 uses
the original TCP HTTP engine; both inherited listener backends are removed.
Public HTTP/3 selection still uses System.Net.Quic for its QUIC transport.
Native-provider integration, Alt-Svc discovery and final resource/performance
acceptance remain unfinished; TCP replacement does not establish their completion.

## Runtime memory limitation

The tested .NET 10.0.12 `System.Net.Quic` retains a native send-buffer allocation
for each disposed stream that sent data. Long-running HTTP/3 traffic therefore
has a known runtime memory-growth risk. HTTP/1.1 and HTTP/2 do not use this path.
The [upstream fix](https://github.com/dotnet/runtime/pull/135591) merged into
runtime `main` on 13 October 2026, but no .NET 10 servicing backport or released
fixed runtime has been verified. Runtime 10.0.13, released the same day, does not
contain it: the fix's merge commit is not in the `v10.0.13` tag. An upstream merge
does not update an installed runtime. Until a fixed runtime is verified, leave
optional HTTP/3 disabled where it is unnecessary or plan bounded process lifetimes
for affected deployments.
See the [attribution and verification status](../project/http-engine-endurance.md#upstream-fix-merged-13-october-2026)
and [tracking issue278](https://github.com/WilliamSmithEdward/embedio-neo/issues/278).

## Server

This complete .NET 10 program expects a PFX path and optional password in the
process environment. Use a certificate valid for `localhost` and trusted by the
client. Reference the unreleased source project when trying this branch.

```csharp
using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using EmbedIO;

var path = Environment.GetEnvironmentVariable("EMBEDIO_TLS_CERTIFICATE")
    ?? throw new InvalidOperationException("Set EMBEDIO_TLS_CERTIFICATE to your PFX path.");
var password = Environment.GetEnvironmentVariable("EMBEDIO_TLS_PASSWORD");
using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    path, password, X509KeyStorageFlags.Exportable);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
using var server = new WebServer(options => options
    .WithMode(HttpListenerMode.EmbedIOHttp3)
    .WithCertificate(certificate)
    .WithUrlPrefix("https://localhost:8443/"))
    .OnGet("/hello", context => context.SendStringAsync(
        "Hello over HTTP/3", "text/plain", WebServer.Utf8NoBomEncoding));
await server.RunAsync(stop.Token);
```

A .NET client can require HTTP/3 without silently falling back:

```csharp
using var client = new System.Net.Http.HttpClient
{
    DefaultRequestVersion = System.Net.HttpVersion.Version30,
    DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact
};
Console.WriteLine(await client.GetStringAsync("https://localhost:8443/hello"));
```

## Client negotiation and JSON API checks

A default .NET HttpClient does not opt into higher HTTP versions. Set
DefaultVersionPolicy to RequestVersionOrHigher to permit negotiation above its
requested version. HTTPS and working QUIC support are necessary for HTTP/3;
application-arranged Alt-Svc can enable later requests to use it. Explicit
RequestVersionExact remains useful when testing one protocol and refusing a
fallback. These are client settings; Combined mode does not automatically emit
Alt-Svc. See [.NET's version policy](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpversionpolicy?view=net-10.0).

Python's standard [http.client](https://docs.python.org/3/library/http.client.html)
uses HTTP/1.1. [HTTPX](https://www.python-httpx.org/http2/) offers optional HTTP/2,
while [aioquic](https://aioquic.readthedocs.io/en/latest/) supplies HTTP/3 APIs.
These libraries do not share one automatic negotiation policy.

The current source passed 43 real JSON GET/POST requests in an owned Linux
.NET 10.0.12 fixture. A WebApiController exercised query binding, JsonData body
binding and the default JSON serializer, including Unicode and a 4 KiB body.
The .NET default client used HTTP/1.1; exact HTTP/2 cleartext/TLS and exact
HTTP/3 passed. With RequestVersionOrHigher and valid application-advertised
Alt-Svc, the first .NET request used HTTP/2 and the following ten used HTTP/3.
Python's standard client retained HTTP/1.1; independent hyper-h2 cleartext/TLS
and aioquic HTTP/3 clients passed. Connection reuse was verified within each
protocol. HTTPX was documented but not executed. These client results do not
establish normal browser discovery or every deployment's QUIC capability.

## Current listener behavior

One listener may own multiple HTTPS prefixes and UDP endpoints. `localhost` binds
IPv4 loopback and, when enabled and available, IPv6 loopback. Wildcard hosts use the
configured wildcard address family. Other DNS hosts bind their first resolved
address; DNS failures do not widen the binding to all interfaces. Routing checks
the receiving endpoint, authority and path. A prefix on another port cannot match
a request arriving on this endpoint. Requests outside the registered prefixes get
404. Multiple listener instances do not currently share the same UDP binding;
combine their prefixes/modules in one WebServer instead.

All bindings must succeed before accept loops start. Failed startup releases its
earlier bindings. Failed peer TLS handshakes do not stop healthy acceptance.
The listener bounds pending application contexts at 256, active connections at
256, and each connection's inbound request-stream credit at 128. These limits
are internal defaults in this increment, not tuned performance recommendations.

`Stop` and disposal cancel active requests, release pending accepts and close
bindings. A directly used `IHttpListener` can restart after Stop; WebServer itself
retains its existing one-run lifecycle. Prefix changes require a stopped listener.
A simultaneous restart while cleanup is running is rejected explicitly.

Restarting on an endpoint this process released moments ago is bridged by a
bounded retry: the native QUIC library can keep the UDP port bound briefly after
disposal returns (a deferred socket close on macOS, and recently accepted
connections on every platform), so a bind that fails with `AddressAlreadyInUse`
for an endpoint released within the last second is retried every millisecond
until that second ends. Other errors, endpoints this process did not release and
conflicts that persist beyond the window fail as before. This is an unreleased
workaround for a native dependency limit, tracked for retirement in
[QUIC listener disposal, rebind and workaround retirement](../project/quic-lifetime.md).
Application response `KeepAlive=false` initiates the connection driver's bounded
GOAWAY drain with its existing 30-second application-triggered deadline. A
listener drain has a separate host-specified abort deadline.

For an explicit listener-wide drain, call `WebServer.DrainAsync` from the host's
shutdown path, outside a request callback. Replace the server sample's final
`await server.RunAsync(stop.Token)` with a host-managed run task:

```csharp
// server is the HTTP/3 WebServer configured above; keep its run token active.
var running = server.RunAsync(stop.Token);
// When the host decides to stop accepting work:
await server.DrainAsync(TimeSpan.FromSeconds(10));
await running;
```

Draining stops new connections, sends GOAWAY on existing connections and lets
accepted responses finish. Later requests are rejected with H3_REQUEST_REJECTED.
The deadline closes connections that have not finished, including long-lived
WebSockets; it is not an unlimited wait for application callbacks. To close
WebSockets with 1001 Going Away when the drain starts instead, see
[WebSockets during a graceful drain](graceful-drain-websockets.md). Cooperative
clients can close after consuming GOAWAY and their responses. Calls made together
share the first drain deadline. A cancellation token supplied to `DrainAsync`
aborts remaining connections and completes that call with cancellation after
transport cleanup. A token already canceled before the call has no side effects.
Canceling `RunAsync`, calling `Listener.Stop`, or disposing the server still stops
immediately and can interrupt a drain. Applications must honor their context
cancellation token; arbitrary application code cannot be forcibly terminated.

The operation supports the modern HTTP/3 listener and managed TCP listeners.
Exclusively owned TCP endpoints close connection admission and use HTTP/2 GOAWAY.
Shared endpoints retain sibling admission and reject new requests for the draining
listener at its registration boundary. Accepted HTTP/1.1 responses and HTTP/2
streams can finish; shared HTTP/2 drain waits only for that owner's contexts and
does not send connection-wide GOAWAY. Combined mode coordinates these TCP semantics
with QUIC and a common deadline. Microsoft mode remains unsupported. Adding prefixes or restarting a TCP listener during its drain is
rejected; await completion before restarting.

Exclusive TCP drain closes owned transports; shared HTTP/2 drain retains sibling
transports and completes or aborts only the draining owner's contexts. Neither
operation can terminate arbitrary callbacks. HTTP/2 contexts receive cancellation
when their owner is aborted. Existing HTTP/1.1 context
cancellation follows the run token, so an application waiting independently must
still arrange its own cancellation. Mixed shared/exclusive endpoint regressions
cover accepted responses and completion/abort paths on HTTP/1.1 and HTTP/2. TLS,
body and slow-peer validation remain under development.

The timeout must be positive and within the timer range
(at most 4,294,967,294 milliseconds). An HTTP/3 listener that has not started or
has already stopped has no connections to drain. Dispose the server after its
run task completes to release module/session resources.

Priorities/datagrams, dynamic response QPACK,
discovery and broader shared-endpoint validation remain under development. No throughput
or latency improvement is claimed by these interoperability tests.

## WebSockets over HTTP/3

The listener advertises extended CONNECT support (RFC 9220) and routes
`:protocol=websocket` requests through existing WebSocket modules. Successful
negotiation returns HTTP 200; subprotocols and cookies use the shared adapters.
Other valid extended CONNECT protocols reach application routing; an unmatched
route returns the ordinary 404. See [CONNECT capsules](capsule-tunnels.md).
A WebSocket version other
than 13 returns 400 with `Sec-WebSocket-Version: 13`.

The .NET 10 ClientWebSocket implementation negotiates only through HTTP/2;
selecting HTTP/3 on that client does not provide a working HTTP/3 WebSocket
client. The wire regression fixture uses a QUIC HTTP/3 stream with the BCL
WebSocket framing implementation on top. It checks text/binary messages,
fragmentation across a UTF-8 sequence, clean stream FIN, client abort and healthy
sibling requests. Its literal request encoder is independent; response field
inspection uses the project's QPACK decoder. This is scoped interoperability
evidence, not a claim of complete WebSocket conformance or performance.

References: [RFC 9220](https://www.rfc-editor.org/rfc/rfc9220.html) and
[.NET 10.0.12 client implementation](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.WebSockets.Client/src/System/Net/WebSockets/WebSocketHandle.Managed.cs).
