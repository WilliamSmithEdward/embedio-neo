# Rotating the server certificate

A long-running HTTPS server needs a new certificate before the old one expires.
`ServerCertificateSource` lets the application install it without restarting the
server: each new TLS handshake uses the certificate that is current when it
starts, and connections that are already open keep the one they started with.

It is opt-in through `WebServerOptions.CertificateSource` (or
`WithCertificateSource`). Servers configured with `Certificate` behave as before.

This is new in Neo v2; the published 1.0.x packages do not include it.

## A complete program

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Net;

// A renewal tool, such as an ACME client, replaces this file; the server picks it up.
var path = args.Length > 0 ? args[0] : "server.pfx";
var password = Environment.GetEnvironmentVariable("EMBEDIO_TLS_PASSWORD");
var reloadInterval = TimeSpan.FromHours(1);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var certificates = new ServerCertificateSource(Load(path, password));
using var server = new WebServer(options => options
        .WithMode(HttpListenerMode.EmbedIO)
        .WithUrlPrefix("https://localhost:8443/")
        .WithCertificateSource(certificates))
    .WithModule(new ActionModule("/", HttpVerbs.Get,
        context => context.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding)));

var running = server.RunAsync(cancellation.Token);
Console.WriteLine(new Uri("https://localhost:8443/"));

try
{
    while (!running.IsCompleted)
    {
        var nextReload = Task.Delay(reloadInterval, cancellation.Token);
        if (await Task.WhenAny(running, nextReload) == running)
            break;
        await nextReload;

        X509Certificate2? renewed = null;
        try
        {
            renewed = Load(path, password);
            if (renewed.Thumbprint != certificates.Current.Thumbprint)
            {
                certificates.Publish(renewed);
                renewed = null; // The source and active connections retain this certificate.
            }
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException or IOException)
        {
            // The current certificate stays in use; try again at the next interval.
            Console.Error.WriteLine("Certificate not replaced: " + error.Message);
        }
        finally
        {
            renewed?.Dispose(); // Unchanged or rejected certificate, never published.
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}
finally
{
    cancellation.Cancel();
    await running; // Observe startup failures and completion without waiting for the reload timer.
}
static X509Certificate2 Load(string path, string? password)
    => X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
```

The program publishes only a certificate that differs from the current one.
Republishing the same certificate is harmless, but skipping it avoids needless
work. Replaced certificates are not disposed by the program because
connections opened before a replacement may still be using them; the garbage
collector releases them once nothing does.

## What `Publish` accepts

`Publish`, like the constructor, rejects a certificate and keeps the current one
when the certificate:

- has no private key, or a private key that cannot be opened;
- is not yet valid, or has expired, at the time of the call;
- has an enhanced key usage extension that does not include server authentication.

It throws `ArgumentException` (`ArgumentNullException` for `null`). It does not
check that clients will trust the certificate or that its names match the URLs
clients use; issue certificates for the host names you serve.

## Where a new certificate applies

| Listener | When a published certificate is used |
| --- | --- |
| HTTP/1.1 and HTTP/2 over TCP (`EmbedIO` mode) | The next TLS handshake on any endpoint of the server. |
| HTTP/3 (`EmbedIOHttp3` mode) | The next QUIC handshake. |
| Combined hosting (`EmbedIOCombined` mode) | One source serves TCP and QUIC, so both switch together. |

One source can serve several servers. Servers with separate sources rotate
independently. Applications using the listener without `WebServer` can call
`EmbedIO.Net.HttpListener.CreateWithCertificateSource(certificates)`. The factory
borrows the source and its certificates. The separately available
`HttpListener(certificates, tlsPolicy)` constructor configures a source together
with an explicit TLS policy.

The .NET Standard 2.0 asset supports rotation for TCP listeners. HTTP/3
requires the .NET 10 asset.

### HTTP/3 transport selection

EmbedIO.Quic captures the current fallback certificate
per handshake before parsing the ClientHello. SNI selection and client-certificate
policy use that same production factory. Windows retains non-exportable Schannel
key support; Unix TLS uses an owned PKCS#12 copy and requires an exportable key.
Existing established connections retain their original certificate selection.

The following native-configuration behavior concerns the earlier MsQuic provider
in packages through 2.16.1; 2.17.0 and later no longer contain it.
The native MsQuic binding loads a certificate into an MsQuic configuration once,
so it builds a new configuration when the next connection arrives after a
different certificate was published. Republishing a certificate with the same
thumbprint reuses the current configuration. Outside Windows, MsQuic receives an exported PKCS#12 copy of
the certificate, so the key must be exportable: load PFX files with
`X509KeyStorageFlags.Exportable`, as the program above does. If a published
certificate cannot be loaded, native HTTP/3 connections keep using the previous
certificate and the failure is logged once; publish a usable certificate to
recover. Configurations of replaced certificates are released when the server
stops.

## Other TLS settings

Client certificates, protocol versions and choosing the certificate by server
name (SNI) are configured with a [TLS policy](tls-policy.md), which works with a
`ServerCertificateSource`: a published certificate keeps the policy's
requirements, and the policy's SNI selector falls back to the source's current
certificate.
