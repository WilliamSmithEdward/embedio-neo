# HTTPS on desktop and .NET MAUI

EmbedIO's managed listener uses the runtime's `SslStream` for HTTPS. Supply an
`X509Certificate2` containing its private key and select `HttpListenerMode.EmbedIO`:

```csharp
using var certificate = LoadServerCertificate(); // App-owned provisioning.
using var server = new WebServer(options => options
    .WithMode(HttpListenerMode.EmbedIO)
    .WithUrlPrefix("https://*:8443/")
    .WithCertificate(certificate))
    .WithStaticFolder("/", htmlDirectory, true);
await server.RunAsync(cancellationToken);
```

`LoadServerCertificate` represents your app's provisioning code. On .NET 10,
`X509CertificateLoader.LoadPkcs12FromFile` can load a PFX with its password;
on .NET Standard 2.0, use the corresponding `X509Certificate2` constructor.
Private-key storage flags and key import support depend on the deployment runtime.
Keep the certificate alive until the server stops and is disposed. Protect the
PFX/password using platform-appropriate secure storage; do not ship a shared
production private key inside an APK or source repository.

The client must trust the issuer and connect using a DNS name or IP address
present in the certificate's Subject Alternative Name. A self-signed certificate
needs explicit client trust. Do not use an accept-all validation callback in a
production client. HTTPS encrypts traffic; configure application authentication
and authorization separately before exposing privileged routes.

TLS protocol selection is delegated to the runtime/OS using `SslProtocols.None`.
It does not mean plaintext or enable every protocol. Actual availability depends
on the OS security provider and its policy. The listener performs authentication
asynchronously, within the existing 90-second first-request timeout; clients that
stall the TLS handshake do not hold the socket accept callback. Stopping the
listener closes pending connections.

## Platform validation

The initial implementation and tests are tracked by
[feature #26](https://github.com/WilliamSmithEdward/embedio-neo/issues/26).
Until CI results are recorded, configured coverage is not a verified support claim.

| Platform/app model | Coverage and current limits |
| --- | --- |
| Windows, Linux and macOS / .NET 10 | Real HTTPS responses, trusted leaf/hostname checks, default rejection of an untrusted certificate, keep-alive, malformed/stalled clients and shutdown in the regression suite. CI runs on Windows 2025, Ubuntu 24.04 and macOS 15. |
| MAUI Android / .NET 10 | The Android 10/API 29 fixture imports a private-key PFX and verifies HTTPS using `SocketsHttpHandler`, alongside existing HTTP lifecycle coverage. Emulator execution is required. |
| MAUI Mac Catalyst / .NET 10 | The sandboxed macOS 26 fixture adds an HTTPS client/listener test alongside the HTTP/WebView smoke. Signed app execution is required. |
| MAUI Windows and iOS | The four-platform native HTTPS fixture exercises the normal client, WebView and a separate strict HTTPS client. Actual execution results are recorded in feature #26; a build pass alone does not establish support. |
| .NET Standard 2.0 / Xamarin / older Android | Target is retained. Scobie's Android 7.1.2/Xamarin.Forms environment is unverified. Modern MAUI results do not prove legacy TLS-provider support. |

See the [MAUI HTTPS validation guide](../platforms/maui-https-validation.md) for
the four-platform fixture, disposable trust provisioning and physical-device probe.
The original transport fixtures use test-only self-signed certificates and
exact-leaf trust pins. The four-platform fixture additionally provisions a
disposable test CA and verifies normal native-client and WebView trust. Physical
device-browser trust and LAN access still require device execution.

For MAUI Android, keep the server owned by the application rather than an
Activity, retain the `INTERNET` manifest permission, and follow the
[lifecycle guide](../platforms/maui-android.md). For Mac Catalyst, retain sandbox
network server/client entitlements from the
[platform guide](../platforms/maui-mac-catalyst.md). Use an externally reachable
prefix only after testing network access and client trust on the target device.

## Windows certificate helpers

`AutoLoadCertificate` and `AutoRegisterCertificate` remain Windows-only helpers
for certificate stores and `netsh` HTTP.sys bindings. The Microsoft listener uses
OS configuration; supplying a certificate does not configure HTTP.sys by itself.
Portable managed-listener hosting uses `WithCertificate` and leaves those helpers
disabled. Existing default listener selection and HTTP configuration are unchanged.

Report deployment problems with the OS/API level, .NET/MAUI runtime, listener
mode, key-import flags and the complete exception. Omit passwords and private keys.
