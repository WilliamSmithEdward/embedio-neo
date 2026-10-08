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

Configure HTTPS prefixes and a server certificate containing its private key.
Keep the certificate alive until the server is disposed. The OpenSSL QUIC backend
used in validation requires an exportable key; choose provisioning appropriate to
your host and protect the certificate/password. Use a DNS name matching the
certificate and configure normal client trust. IP-literal URLs worked in the
Windows fixture but failed TLS setup with the tested Linux .NET 10.0.12/MsQuic
2.6.2 client; the portable examples and tests use `localhost`.

HTTP/3 uses UDP. This mode does not bind a TCP listener or accept HTTP/1/HTTP/2.
It does not currently advertise Alt-Svc or configure DNS HTTPS records. Clients
must explicitly select HTTP/3, or discovery must be arranged by the application.
Any network permission must allow the intended UDP endpoint.

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
Application response `KeepAlive=false` initiates the connection driver's bounded
GOAWAY drain; listener-wide graceful shutdown is not yet exposed.

Priorities/datagrams, dynamic response QPACK,
discovery and combined-protocol hosting remain under development. No throughput
or latency improvement is claimed by these interoperability tests.

## WebSockets over HTTP/3

The listener advertises extended CONNECT support (RFC 9220) and routes
`:protocol=websocket` requests through existing WebSocket modules. Successful
negotiation returns HTTP 200; subprotocols and cookies use the shared adapters.
Unsupported extended CONNECT protocols return 501. A WebSocket version other
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
