# Basic authentication on the Microsoft listener

[Upstream #524](https://github.com/unosquare/embedio/issues/524) reported a valid
Basic-authenticated POST returning 500 when using the Microsoft listener.
gabriele-ricci-kyklos supplied the server, controller, request and restricted-header
exception, and confirmed the managed listener worked. rdeago
[identified the native response method](https://github.com/unosquare/embedio/issues/524#issuecomment-859607445)
as an alternative to writing the restricted collection directly.

## Cause and correction

The Basic authentication module sets `WWW-Authenticate` before checking credentials.
On .NET Framework, the Microsoft response's `Headers.Set` rejects this restricted
header, so the module fails before either accepting valid credentials or returning
its intended 401 for invalid credentials.

The fix sends this module's challenge through the native response's public
`HttpListenerResponse.AddHeader` method. Microsoft's
[method documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistenerresponse.addheader)
describes replacement of an existing value. The adapter is internal; it does not
add an `IHttpResponse` member, reflection workaround or new production dependency.
Managed and custom response implementations retain the existing collection path.

The module still returns its challenge on successful responses, decodes credentials
as before, preserves realm formatting and stops unauthorized requests before the
protected handler. Two accepted layered modules replace the earlier challenge;
rejection by the first module retains that module's challenge and stops dispatch.
This correction does not change native listener authentication schemes or introduce
a new authentication provider.

## Using the fix

Keep your existing server/controller configuration. The original pattern remains:

```csharp
// Configuration fragment: retain your app-owned lifetime and RunAsync cancellation.
new WebServer(options => options
    .WithUrlPrefix("http://localhost:9696/")
    .WithMode(HttpListenerMode.Microsoft))
    .WithLocalSessionManager()
    .WithModule(new BasicAuthenticationModule("/").WithAccount("user", "password"))
    .WithWebApi("/api", api => api.WithController<TestController>())
```

This is a partial configuration, with `EmbedIO`, `EmbedIO.Authentication` and
`EmbedIO.WebApi` imports; use your actual controller, route and app-owned lifetime.
The correction is on main after its PR merges and is **not included in an existing
NuGet release by this change**. No package upgrade version is promised here.
Before an authorized release containing the fix, the reporter's managed-listener
configuration remains an existing alternative: use `HttpListenerMode.EmbedIO` if
its endpoint/certificate behavior is suitable for your application. Changing
listener mode is an application decision, particularly for Windows HTTP.sys URL
reservations and certificate configuration; see
[HTTPS guidance](../platforms/maui-https-validation.md).

Basic authentication transmits credentials encoded, not encrypted. Use trusted
HTTPS for credentials outside a controlled local demonstration and an appropriate
application-owned account/provider policy. This fix does not turn the simple
in-memory account dictionary into a password storage or identity management system.

## Reproduction and regression coverage

A .NET Framework host loading current .NET Standard 2.0 core reproduced the exact
500 and `WWW-Authenticate` restricted-header exception on the original valid,
empty-body POST. The corrected request returns 200 with JSON `true` and retains
the Basic challenge. This is a runtime-specific reproduction, not merely an
in-process mock or a modern desktop pass.

Shared real-HTTP coverage checks managed/native .NET 10 listeners and a test-only
Windows .NET Framework host compiled for net472, using the retained Standard core.
Each single/layered-module run sends ten requests: valid POST, missing credentials,
wrong password, malformed Base64, malformed/other scheme, missing account,
mixed-case Basic scheme, UTF-8 credentials with a colon-containing password, and
a healthy valid request after denials. Checks require exact status/body/challenge,
one challenge value, protected-handler isolation, local sessions and compressed
response decoding. Shutdown is bounded and observed.

The legacy fixture is outside the ordinary solution and all shipped packages. Its
reference-assembly package is pinned and locked for development only. Windows CI
runs its real native listener and uploads `legacy-authentication.json` with the
desktop test artifact. A net472 compilation does not claim execution on the original
.NET Framework 4.7.2 runtime; the installed host version is recorded separately.

Direct application writes to other restricted native headers and arbitrary wrapper
implementations are outside this focused module correction. If a failure remains,
provide the runtime/listener mode, minimal server/controller setup and sanitized
exception. Do not include real passwords or Authorization headers.
