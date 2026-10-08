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

HTTP/3 WebSockets/extended CONNECT, priorities/datagrams, dynamic response QPACK,
discovery and combined-protocol hosting remain under development. No throughput
or latency improvement is claimed by these interoperability tests.
