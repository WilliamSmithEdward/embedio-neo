# TLS policy: client certificates, protocols and SNI

`ServerTlsPolicy` configures the TLS handshakes of HTTPS listeners beyond the
server certificate:

- ask clients for a certificate, require one, and decide which certificates to accept;
- limit the TLS protocol versions;
- on the .NET 10 asset, choose the server certificate by the host name the client
  asks for (SNI).

It is opt-in through `WebServerOptions.TlsPolicy` (or `WithTlsPolicy`). A server
without a policy, or with a new `ServerTlsPolicy()`, behaves as before: no client
certificate is requested, TLS 1.2 or later is allowed where supported, and
the configured certificate is used. It combines with a fixed `Certificate` or a
[`ServerCertificateSource`](certificate-rotation.md).

This is new in Neo v2; the published 1.0.x packages do not include it.

## Sharing a TCP listening endpoint

TLS authentication happens before the HTTP request path selects a logical server.
HTTPS listeners sharing an address and port must therefore have compatible TLS
policies. Registration refuses an incompatible policy before publishing that
listener's routes, regardless of which listener starts first. Earlier listeners
keep their own registrations.

Use separate ports when the client-certificate requirement, revocation setting,
protocol configuration or certificate-selection policy differs. To share an
endpoint, use the same locked policy, or policies with the same settings and the
same validator and selector delegate instances. Separately constructed callbacks
are not treated as equivalent, even when their code looks identical. A null policy
and a fresh default policy remain compatible. An explicit protocol mask is also
compatible when it matches the default versions supported by the executing
runtime. Comparison uses the same normalization as authentication and never
invokes callbacks.

Registration also compares the server certificate itself, because the endpoint
presents one certificate to every connection, whichever logical owner the request
path selects afterwards. Listeners share an HTTPS endpoint only when they present
the same certificate: the same `ServerCertificateSource` instance, or certificates
that compare equal by hash, so that a certificate loaded twice from the same bytes,
or the one a combined listener wraps internally, still matches. A source the
application can rotate matches only itself, even while it holds the other owner's
certificate, because it may rotate away from it while the endpoint is shared. A
second owner with a different certificate is refused before its routes are
published, with "different server certificates" in the `HttpListenerException`,
and the first owner keeps serving. Owners that need rotation borrow one shared
source.

An https prefix on a listener that has neither a certificate nor a source fails at
`Start` with an `InvalidOperationException`, and `WebServer.RunAsync` faults with
it, instead of opening the port and dropping every connection at the handshake.
The HTTP/3 and combined listeners already refuse a missing certificate when they
are constructed. Configure the certificate or the source before starting.

## Cleartext HTTP/2 and the `:scheme` pseudo-header

Over a cleartext HTTP/2 connection (prior knowledge), the managed engine accepts a
request whose `:scheme` is `https`. A TLS-terminating proxy that forwards h2c to
EmbedIO sends the scheme the client used, and rejecting it would break every such
deployment; RFC 9113 section 8.3.1 defines `:scheme` as the scheme of the target
URI and does not tie it to the connection's transport. The request URL carries the
scheme as sent, `IHttpRequest.IsSecureConnection` reports the actual transport
(false over cleartext), and an application that needs to know whether the client's
own hop was encrypted resolves that through the forwarded-header module's trusted
proxies rather than through the URL scheme.

## A complete program: requiring client certificates

```csharp
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Net;

// The server's certificate, and the SHA-256 hashes of the client certificates allowed in.
var serverPfx = args.Length > 0 ? args[0] : "server.pfx";
var allowed = new HashSet<string>(args.Length > 1 ? args[1].Split(',') : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

using var certificate = X509CertificateLoader.LoadPkcs12FromFile(serverPfx, Environment.GetEnvironmentVariable("EMBEDIO_TLS_PASSWORD"));
var policy = new ServerTlsPolicy
{
    ClientCertificateMode = ClientCertificateMode.Require,
    // Accept exactly the listed certificates. Without a validator, a client certificate must
    // chain to a root the operating system trusts.
    ClientCertificateValidator = (client, _, _) => allowed.Contains(client.GetCertHashString(HashAlgorithmName.SHA256)),
};

using var server = new WebServer(options => options
        .WithMode(HttpListenerMode.EmbedIO)
        .WithUrlPrefix("https://localhost:8443/")
        .WithCertificate(certificate)
        .WithTlsPolicy(policy))
    .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
    {
        // Required and validated, so every request has one.
        var client = context.Request.GetClientCertificate();
        return context.SendStringAsync("hello, " + client?.GetNameInfo(X509NameType.SimpleName, false),
            "text/plain", WebServer.Utf8NoBomEncoding);
    }));

Console.WriteLine(new Uri("https://localhost:8443/"));
await server.RunAsync(cancellation.Token);
```

A client without an allowed certificate never reaches the handler: its handshake
fails. Depending on the TLS version, the client may see the failure while
connecting or on its first read.

## Client certificates

| `ClientCertificateMode` | Client without a certificate | Client with a certificate |
| --- | --- | --- |
| `None` (default) | Accepted. | Not asked for one; `GetClientCertificate` returns `null`. |
| `Allow` | Accepted; `GetClientCertificate` returns `null`. | Accepted only if it passes validation. |
| `Require` | Refused. | Accepted only if it passes validation. |

`ClientCertificateValidator` decides whether a certificate passes. It receives the
certificate, the chain the runtime built and the `SslPolicyErrors` it found, and
returns `true` to accept. Without a validator, a certificate passes only with no
errors, so a self-signed or privately issued certificate is refused unless its root
is trusted by the operating system. The validator runs during the handshake, may run
concurrently, and must not keep the chain. An exception it throws refuses that
handshake and is logged; the server keeps serving other clients.

`CheckCertificateRevocation = true` checks client certificates' revocation status
online while building their chain; a failure shows up as a chain error.

`context.Request.GetClientCertificate()` returns the client's certificate for every
request on the connection, over HTTP/1.1, HTTP/2 and HTTP/3. It holds no private key.
Do not dispose it: requests on the same connection share it.

## Protocol versions

`EnabledProtocols` limits the TLS versions of TCP listeners. Its default,
`SslProtocols.None`, allows TLS 1.2 and supported TLS 1.3, as described in the
[HTTPS guide](https.md). Explicit masks can restrict those versions; legacy or
unknown protocol bits are rejected before changing the policy. TLS 1.0 and TLS 1.1
cannot be enabled through this property. QUIC always uses TLS 1.3, so an HTTP/3 or combined server
refuses to start with a value that excludes it.

## Choosing the certificate by server name (SNI)

```csharp
var policy = new ServerTlsPolicy
{
    // Return null to use the server's configured certificate.
    ServerCertificateSelector = name => name switch
    {
        "api.example.com" => apiCertificate,
        "www.example.com" => siteCertificate,
        _ => null,
    },
};
```

The selector receives the host name the client asked for, or `null` when it sent
none, for example when connecting by IP address. It returns a certificate with its
private key, or `null` for the server's configured certificate: the fixed
`Certificate`, or the current certificate of a `ServerCertificateSource`. An exception
it throws refuses that handshake and is logged. The application owns the returned
certificates; keep them alive while the server runs.

SNI selects a certificate only. Requests are still routed by the server's URL
prefixes and their `Host` header.

## What each listener supports

| Setting | TCP, .NET 10 asset | TCP, .NET Standard 2.0 asset | HTTP/3, default binding | HTTP/3, native binding |
| --- | --- | --- | --- | --- |
| Client certificates and validator | Yes | Yes | Yes | No: refuses to start |
| `CheckCertificateRevocation` | Yes | Yes | Yes | No: refuses to start with client certificates |
| `EnabledProtocols` | Yes | Yes | TLS 1.3 only | TLS 1.3 only |
| `ServerCertificateSelector` (SNI) | Yes | No: refuses to start | Yes | No: refuses to start |

A server refuses a setting it cannot honor when it is created, rather than ignoring
it. Combined hosting applies one policy to both its TCP and QUIC listeners.

The removed Microsoft `HttpListener` mode (HTTP.sys) is not involved: its TLS settings
belong to Windows configuration, which this policy never changed.

## Validation

The 23 policy tests perform real TLS handshakes, and CI runs them on Windows, Linux and macOS. They cover:

- required, allowed and absent client certificates over HTTP/1.1, HTTP/2 and HTTP/3,
  and their default refusal without a validator;
- validator errors and exceptions, 64 concurrent clients each seeing its own
  certificate, and abandoned handshakes;
- protocol limits, SNI selection and selector exceptions;
- refusals of unsupported settings, and certificate rotation under a policy.
