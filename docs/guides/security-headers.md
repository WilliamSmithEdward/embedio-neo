# Opt-in response security headers (unreleased)

These APIs are on the feature branch and are **not in NuGet 2.12.0**.
Nothing is installed by default. Existing manually written headers remain unchanged
until an application explicitly selects a policy.

## Configure the server and controllers

`SecurityHeadersPolicy` is immutable. Construct policies once, before starting the
server. CSP and Permissions-Policy directives are application-owned configuration;
the library bounds and validates field text, not the semantics of every directive.
Do not build policies from request headers, query strings or untrusted tenants.

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.WebApi;

internal static class Program
{
    private static async Task Main()
    {
        var baseline = new SecurityHeadersPolicy(
            contentSecurityPolicy: "default-src 'self'; frame-ancestors 'none'");
        var page = new SecurityHeadersPolicy(
            contentSecurityPolicy: "default-src 'none'; script-src 'nonce-{nonce}'; frame-ancestors 'none'",
            permissionsPolicy: "camera=(), microphone=()");
        var api = new WebApiModule("/api", ResponseSerializer.None(false));
        api.ConfigureSecurityHeaders(new Dictionary<string, SecurityHeadersPolicy>
        {
            ["baseline"] = baseline,
            ["page"] = page
        }, defaultPolicy: "baseline");
        api.RegisterController(PageBindings.PageController);
        using var server = new WebServer(options =>
        {
            options.WithUrlPrefix("http://127.0.0.1:9696/");
            options.SecurityHeaders = baseline;
        }).WithModule(api);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
        await server.RunAsync(stop.Token);
    }
}

[GeneratedControllerBinding(typeof(PageController))]
internal static partial class PageBindings { }

[SecurityHeaders("baseline")]
public sealed class PageController : WebApiController
{
    [Route(HttpVerbs.Get, "/page"), SecurityHeaders("page")]
    public async Task Page()
    {
        var nonce = WebUtility.HtmlEncode(HttpContext.GetSecurityHeadersNonce());
        await HttpContext.SendStringAsync(
            "<!doctype html><script nonce=\"" + nonce + "\">console.log('trusted application code');</script>",
            "text/html", WebServer.Utf8NoBomEncoding);
    }
}
```

Install the package's source generator as usual; direct source-project consumers
must reference `EmbedIO.Generators` as an analyzer. Reflection registration with
`RegisterController<PageController>()` supports the same selection and is not AOT-safe.

Selection precedence is method attribute, inherited class attribute, module default,
then the policy already selected by the server or preceding middleware. Inherited
method attributes apply to overrides. Policy names are case-sensitive; routes retain
the module's existing case sensitivity. Registries are copied at configuration.
Configure before registering any controllers; unknown names fail registration before
adding routes. Invalid generated names produce `EMBEDIO017`.

A selected profile **replaces all seven managed fields**, including omissions. A null
CSP, framing or HSTS field removes the previously selected field. This is not a field
merge. To relax a route explicitly, register a named minimal profile. Code cannot
change profiles after exposing a nonce or committing headers. The route selection
runs before controller authorization, hooks, activation and output-cache replay.
Independent middleware that rejects a request before routing uses the earlier policy.

For a subset of non-controller modules use
`new SecurityHeadersModule("/ui", page)` before those handlers and their caches.
For one fluent handler call `page.Apply(context)` before producing the response.
Server `options.SecurityHeaders` also covers application errors before route dispatch.
Failures rejected by transport parsing before a request context exists are outside
that application policy scope. Error and WebSocket handshake serializers restore
selected fields after ordinary application header clearing. HTTP/1, HTTP/2 and
HTTP/3 apply policies at final-header serialization, including streaming/empty/HEAD
responses. Informational responses and trailers do not select a new document policy.

## Fields and HTTPS

Installing a default-constructed profile emits `X-Content-Type-Options: nosniff`,
`Referrer-Policy: strict-origin-when-cross-origin` and `X-Frame-Options: DENY`.
CSP, report-only CSP, Permissions-Policy and HSTS require explicit values.
`frameOptions` accepts `DENY`, `SAMEORIGIN` or null; coordinate this with CSP
`frame-ancestors`. Referrer values use the standard policy tokens. Each supplied
field is limited to 4,096 visible ASCII characters after nonce expansion, and combined fields to 16 KiB.
Control characters, CR/LF, DEL, non-ASCII and blank fields are rejected at construction.

HSTS requires explicit `hstsMaxAge`; it is emitted only when
`Request.IsSecureConnection` reports an actual secure transport. Forwarded headers,
URL text and the in-process test server's simulated HTTPS address do not establish
TLS. A TLS-terminating proxy should set HSTS at the proxy. Include-subdomains and
preload are separate explicit choices; preload requires subdomains and at least a
year. The library never redirects HTTP, submits domains to a preload list or changes
certificate validation. HSTS is **host-wide**, even when sent from a route. Keep its
settings consistent across route profiles. Omitting a field does not clear a browser's
existing HSTS state; explicit max-age zero has protocol-defined limitations.

## Nonces and caching

Use the exact quoted CSP source `'nonce-{nonce}'` in an enforced and/or report-only
CSP. Fixed nonce sources and malformed placeholders are rejected. The nonce is 32
cryptographically random bytes encoded as Base64, generated once per response and
shared by both CSP fields. `GetSecurityHeadersNonce()` returns that stable value;
call it before writing trusted script/style markup. Attribute-encode it. Never add
nonces to attacker-supplied markup: doing so authorizes that markup. The library
does not rewrite HTML, sanitize content or supply a safe CSP for every application.
A report-only policy observes violations and does not enforce them.

Nonce profiles bypass the built-in output cache **before lookup** and cannot be
stored on completion. They force `Cache-Control: no-store`, `Pragma: no-cache` and a
past Expires, removing ETag/Last-Modified at final serialization. Late manual attempts
to overwrite policy/cache fields do not win. Nonce responses reject 206/304 and
Content-Range rather than binding a fresh nonce to a reused/partial document.
Use static policies for assets that need conditional/range semantics. HEAD receives
its own nonce; do not use HEAD metadata to reuse a different response's HTML.

Select the policy **before entering an output-cache wrapper**. Selecting inside a
handler cannot affect a previously cached response that skips that handler. Existing
static-policy cache entries are bypassed once a nonce policy is selected beforehand.
Static profiles preserve caching, validators, range/HEAD behavior and the current
request's policy fields. Application/CDN/service-worker caches must honor no-store
and must not independently retain nonce-bearing bodies; the framework cannot control
a noncompliant external cache or already persisted application HTML.

Idempotency deliberately replays exact finite response bytes, so nonce profiles are
incompatible. Known controller combinations fail registration; runtime selection and
nonce access fail inside recording, including access through a captured original
context. Use a static API policy for idempotent operations and return a fresh separate
page. See [output caching](output-caching.md) and [idempotency](idempotency.md).

## Custom adapters, validation and limits

First-party listeners and `TestWebServer` prepare headers automatically. An external
`IHttpContext`/`IHttpResponse` adapter must call
`SecurityHeadersPolicy.PrepareResponse(context)` immediately before copying final
headers to its transport, after all application callbacks. It must accurately report
transport security, and prohibit later mutation. Merely calling `Apply` is not a
substitute for this adapter contract. Existing interfaces gain no required members.

See the [security audit](../project/security-headers-audit.md) for scope, evidence and
remaining deployment assumptions. Run the focused NUnit filter
`FullyQualifiedName~SecurityHeaders`. The independent `security-headers` state-machine
campaign overlaps three clients at application entry, switches static/enforced/report-only
profiles, clears shared output storage, and checks nonce/body binding, freshness,
cache isolation and control rejection. Its negative oracle must reject an injected
nonce mismatch:

```text
dotnet run --project test/EmbedIO.Fuzz -c Release -- --security-headers-selftest
dotnet run --project test/EmbedIO.Fuzz -c Release -- --state-machines 1729 500 security-headers
```

Browser behavior depends on document context and browser support. CSP is defense in
depth alongside authentication, authorization, output encoding, TLS and CSRF controls.
See [W3C CSP](https://www.w3.org/TR/CSP3/),
[OWASP CSP guidance](https://cheatsheetseries.owasp.org/cheatsheets/Content_Security_Policy_Cheat_Sheet.html)
and [OWASP response headers](https://cheatsheetseries.owasp.org/cheatsheets/HTTP_Headers_Cheat_Sheet.html).

For reproducible fresh-process measurements, select
`EMBEDIO_LATENCY_SCENARIOS=literal,security-static,security-nonce` and run
`dotnet run --project test/EmbedIO.Performance -c Release -- --endpoint-latency 3`.
The client checks nonce/body binding and the static fields while recording the
same first-server, first-endpoint-after-control and warm samples as the other
latency scenarios. See the audit's local comparison and its sampling limitations.
