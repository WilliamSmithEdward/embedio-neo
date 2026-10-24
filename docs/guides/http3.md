# HTTP/3 listener (Neo v2)

`HttpListenerMode.EmbedIOHttp3` serves normal WebServer modules over QUIC,
including request bodies, routing, responses and sessions. Its transport is
EmbedIO.Quic, added in 2.17.0 by [PR #506](https://github.com/WilliamSmithEdward/embedio-neo/pull/506); packages
through 2.16.1 use the earlier `System.Net.Quic` provider. The [development record](../project/quic-development-and-testing.md)
records what has been validated and what remains.

## Requirements

Use the .NET 10 library asset in a 64-bit desktop
process. Windows uses [Schannel TLS 1.3](https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-)
(Windows 11 / Server 2022 and later).
Linux/macOS use the owned `EmbedIO-Neo.Quic.Native` runtime dependency, selected
transitively by NuGet; publish for the deployment RID when producing a targeted
application. Server startup checks that TLS path directly, without relying on
BCL QUIC availability. Missing Unix assets fail with `PlatformNotSupportedException`;
an incompatible private ABI/version also fails before listening. The .NET Standard
asset continues to reject HTTP/3. CI runs the package consumers on all six runtime
identifiers, and they passed once on macOS 14, the oldest macOS the native build
targets. CI also runs them on the oldest glibc and musl that .NET 10 supports:
glibc 2.27 (Ubuntu 18.04) and musl 1.2.3 (Alpine 3.17), on x64 and Arm64. A Native
AOT application runs only on the glibc it was built against or a newer one, so
publish it on the oldest distribution you deploy to. See
[native runtime preparation](../../native/EmbedIO.Quic.Tls/README.md).

A macOS app signed with the hardened runtime loads `libembedio_quic_tls.dylib` like
any other library it ships: library validation accepts it only when it is signed with
the app's own Team ID, so sign the dylib with your identity before the app, inside
out. The native runtime uses no JIT or writable executable memory, so a Native AOT
app should need no entitlement for HTTP/3. A JIT app needs
`com.apple.security.cs.allow-jit`, as any .NET app does under the hardened runtime.
Only ad-hoc signed apps have been tested so far. An ad-hoc signature has no Team ID,
so such an app also needs `com.apple.security.cs.disable-library-validation`; with it,
both AOT and JIT apps passed. Without it, server construction throws
`PlatformNotSupportedException` because the library was refused. See the
[development record](../project/quic-development-and-testing.md#hardened-runtime-macos-apps)
for what was tested.

For the provider in packages through 2.16.1, use a host where
`QuicListener.IsSupported` is true.
Windows requires TLS 1.3 and Linux/macOS require the system MsQuic prerequisite.
The following runtime and native-library notes apply to that earlier provider.
On Windows, .NET bundles MsQuic, so use runtime 10.0.13 or later. It is the .NET
security release for [CVE-2026-105016](https://msrc.microsoft.com/update-guide/vulnerability/CVE-2026-105016),
a remote code execution vulnerability in Microsoft QUIC that MSRC rates Critical
for .NET installed on Windows. Runtime 10.0.13 bundles `msquic.dll` 2.5.11;
10.0.12 bundled 2.5.10. Linux and macOS load the MsQuic library installed on the
host, which a runtime update does not change.

Configure HTTPS prefixes and a server certificate containing its private key.
Keep the certificate alive until the server is disposed. The Unix TLS path
requires an exportable key; choose provisioning appropriate to
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

HTTP/3 uses UDP. This mode does not bind a TCP listener or accept HTTP/1/HTTP/2,
so no earlier TCP response can advertise HTTP/3, and it configures no DNS HTTPS
records. Clients must select HTTP/3 explicitly, or the application arranges
discovery. Combined mode, below, advertises HTTP/3 itself.
Any network permission must allow the intended UDP endpoint.

## Combined TCP and QUIC hosting

The Neo v2 `HttpListenerMode.EmbedIOCombined` mode serves HTTP/1.1 and
HTTP/2 over TCP and HTTP/3 over UDP from one `WebServer`, using the same HTTPS
prefixes, certificate and application modules. In the server example below,
replace `WithMode(HttpListenerMode.EmbedIOHttp3)` with
`WithMode(HttpListenerMode.EmbedIOCombined)` to use it.

Both transports are required: startup fails if either binding fails, and rolls
back the bindings owned by that attempt. There is no silent TCP-only fallback.
The .NET Standard asset and hosts without the selected transport's TLS support
reject this mode. Only
HTTPS prefixes are accepted. Allow the intended TCP and UDP endpoints in the
host's network policy. The certificate and client trust requirements above apply.

<a id="alt-svc"></a>
Combined mode advertises HTTP/3 the way Kestrel does. Every response, on HTTP/1.1,
HTTP/2 and HTTP/3, carries `Alt-Svc: h3=":8443"; ma=86400`, naming the port the
request arrived on, which serves TCP and QUIC alike, and asking clients to remember
it for a day. Browsers, and .NET clients with `RequestVersionOrHigher`, then send
later requests over HTTP/3: in the tests a .NET client's first request used HTTP/2
and the next ten used HTTP/3. An `Alt-Svc` value the application sets, such as
`clear`, is sent unchanged. `WithoutHttp3Advertisement()`, or
`AdvertiseHttp3 = false`, sends none. Behind a proxy or port mapping that exposes a
different port, set the header yourself or turn the advertisement off. 2.17.0 and
earlier send no Alt-Svc; this is in the source after 2.17.0 and not yet released.

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
The ordinary HTTP/3 binding selects EmbedIO.Quic, and this source no
longer contains the earlier providers. Resource and performance acceptance beyond
the recorded endurance runs remains separate work; TCP replacement does not
establish it.

<a id="runtime-memory-limitation"></a>
## Earlier provider runtime memory limitation

This limitation concerns the previous `System.Net.Quic` provider, which packages
through 2.16.1 use. It does not establish the replacement engine's lifetime
or memory behavior; that has its own acceptance work.

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
client. Reference Neo v2; the published 1.0.x packages do not include HTTP/3.

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
requested version. HTTPS and working QUIC support are necessary for HTTP/3,
and the [Alt-Svc header](#alt-svc) that Combined mode sends lets later requests use
it. Explicit RequestVersionExact remains useful when testing one protocol and
refusing a fallback. See [.NET's version policy](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpversionpolicy?view=net-10.0).

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
The listener bounds pending application contexts at 256, connections at 256 (see
below), and each connection's inbound request-stream credit at 128. These limits are
internal defaults, not tuned performance recommendations. Each stream the client
opens must complete its header within `RequestHeadersTimeout`, 30 seconds by
default; see [connection limits](request-limits.md#connection-limits).

<a id="overload"></a>
Each UDP endpoint holds at most 768 QUIC connections, and their work, TLS handshakes
included, runs on at most eight workers. A new connection is refused before any TLS
work starts while the endpoint holds 768 connections, while queued work waits longer
than 250 ms (the bound MsQuic's workers use by default), or while the listener has no
room for it: its established connections and those still in their handshake fill its
256 connection slots. The client receives an Initial packet with
`CONNECTION_REFUSED` (RFC 9000 section 5.2.2) and fails at once. Established
connections carry on. The server logs refusals as a warning at most every ten
seconds, by reason, with the queue delay and the open connections. This is
unreleased. In 2.17.0 a full endpoint drops new connections' packets until the
clients' handshakes time out, and a connection the listener has no room for
completes its TLS handshake before it is closed with `H3_EXCESSIVE_LOAD`.

`Stop` and disposal cancel active requests, release pending accepts and close
bindings. A directly used `IHttpListener` can restart after Stop; WebServer itself
retains its existing one-run lifecycle. Prefix changes require a stopped listener.
A simultaneous restart while cleanup is running is rejected explicitly.

Stopping releases each UDP endpoint before it returns, so a new listener can bind
it at once. The provider in packages through 2.16.1 retried such a bind for up to a second
because its native QUIC library could keep the port bound briefly after disposal;
this source has no retry. The
[QUIC listener disposal and rebind record](../project/quic-lifetime.md) keeps the
history.
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

<a id="address-validation"></a>
## Address validation and the TLS profile

`WithHttp3AddressValidation()`, or `RequireHttp3AddressValidation = true`, makes the
server answer the first packet of every new connection with a Retry (RFC 9000
section 8.1.2). The Retry carries a token sealed with a key the listener draws at
startup; it binds the client's IP address and expires after ten seconds. The server
keeps no state and starts no TLS work until the client returns the token from the
same address, so spoofed source addresses cannot spend the server's handshake work.
Each new connection costs one more round trip. A token the server sealed that is
expired or sent to the wrong connection ID closes the attempt with `INVALID_TOKEN`;
a token it cannot open, such as another server's, gets a new Retry. It is off by
default, and it needs `EmbedIOHttp3` or `EmbedIOCombined` mode; other modes refuse
to start with it. This option is unreleased; 2.17.0 does not send Retry.

The rest of the TLS profile is deliberately narrow:

- No session resumption. Every connection runs a full TLS 1.3 handshake, and the
  server sends no session tickets. Resumption needs a ticket-encryption key the
  server must create, rotate and protect, on both the Schannel and the OpenSSL
  paths; it would save one certificate signature and the chain per reconnection.
- No 0-RTT. It depends on resumption, and its early data can be replayed (RFC 9001
  section 9.2), so an application would have to choose which requests are safe to
  replay and answer the others with 425 Too Early (RFC 8470). Clients send their
  first request after the handshake.
- On Linux and macOS the certificate's private key must be exportable. The TLS
  runtime there is its own OpenSSL, inside `EmbedIO-Neo.Quic.Native`, and it receives
  the key exported from the certificate; a hardware- or store-held key that cannot be
  exported cannot reach it. Supporting such keys would need a signing callback from
  native code into .NET for every handshake. Windows uses Schannel with the key in
  place, so non-exportable CNG keys work there.

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

## WebTransport

A server configured with `WithWebTransport` also
accepts WebTransport sessions over this listener. See
[WebTransport over HTTP/3](webtransport.md).
