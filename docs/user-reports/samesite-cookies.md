# SameSite response and session cookies

[Upstream #479](https://github.com/unosquare/embedio/issues/479), reported by
AbeniMatteo with follow-up from Joe118, requested an explicit SameSite setting.
Their browser warnings date from 2020; this guide does not assume identical
behavior in today's browsers. The reported `a_session_console` cookie has not
been identified as a Neo cookie; Neo's default session cookie is `__session`.

## Availability and defaults

This describes the new source implementation, tracked in
[Neo #168](https://github.com/WilliamSmithEdward/embedio-neo/issues/168).
It is **unreleased** and is not available in published EmbedIO-Neo 1.0.3.
Use a checkout containing this change until a release is announced.

Existing `SetCookie(cookie)` calls retain their listener's existing validation
and duplicate-cookie semantics. SameSite is omitted unless explicitly selected.
`LocalSessionManager.CookieSameSite` defaults to null, and `CookieSecure` defaults
to false; HttpOnly, names, paths and durations retain their existing defaults.
No listener switch, interface member or production dependency was added.

## A complete local example

From a repository checkout containing this change, create an application:

```sh
dotnet new console --name CookieDemo --framework net10.0 --output TestResults/CookieDemo
dotnet add TestResults/CookieDemo/CookieDemo.csproj reference src/EmbedIO/EmbedIO.csproj
```

Replace `TestResults/CookieDemo/Program.cs` with:

```csharp
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

using var server = new WebServer(o => o
    .WithUrlPrefix("http://localhost:9696/")
    .WithMode(HttpListenerMode.EmbedIO))
    .WithLocalSessionManager(s => s.CookieSameSite = CookieSameSiteMode.Lax)
    .OnGet("/cookie", async context =>
    {
        context.Response.SetCookie(
            new Cookie("visit", "example", "/") { HttpOnly = true },
            CookieSameSiteMode.Lax);
        await context.SendStringAsync("Cookie set.", "text/plain", WebServer.Utf8NoBomEncoding);
    })
    .OnGet("/session", async context =>
    {
        context.Session["seen"] = true;
        await context.SendStringAsync("Session active.", "text/plain", WebServer.Utf8NoBomEncoding);
    });

Console.WriteLine("Open http://localhost:9696/cookie or /session. Press Ctrl+C to stop.");
try
{
    await server.RunAsync(stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
}
```

Run `dotnet run --project TestResults/CookieDemo`. These actions match their base
paths, including children. Check the response fields:

```sh
curl -i http://localhost:9696/cookie
curl -i http://localhost:9696/session
```

The first response contains `visit=example`, `Path=/`, `HttpOnly` and
`SameSite=Lax`, with body `Cookie set.`. The second contains a `__session` cookie
with HttpOnly and SameSite=Lax, with body `Session active.`. Attribute order is
not a contract. The example is local HTTP; configure trusted HTTPS and Secure
cookies for deployed applications. See [HTTPS guidance](../platforms/maui-https-validation.md)
for the distinction between TLS and platform client trust.

## Selecting a policy

`Lax` permits same-site use and certain top-level cross-site navigations;
`Strict` limits use to the same site. `None` permits cross-site use and requires
Secure. Browser privacy settings can still block third-party cookies. These are
site rules, not merely origin/port comparisons. See the maintained
[Set-Cookie reference](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Set-Cookie#samesitesamesite-value).

For an application cookie on an HTTPS endpoint, replace the cookie call with:

```csharp
context.Response.SetCookie(
    new Cookie("visit", "example", "/") { HttpOnly = true, Secure = true },
    CookieSameSiteMode.None);
```

For sessions, replace the configuration callback with:

```csharp
.WithLocalSessionManager(s =>
{
    s.CookieSecure = true;
    s.CookieSameSite = CookieSameSiteMode.None;
})
```

These are replacement snippets, not complete programs. Session settings lock
when the manager starts. None without Secure is rejected before adding an
application cookie, or at session-manager startup. Creation and deletion use the
same selected policy. Do not turn off Secure after selecting None on an
application cookie. No automatic CSRF defense is implied by this setting alone.

The response overload supports the built-in managed and native listener adapters;
foreign response serializers fail explicitly rather than silently ignoring
metadata. Such custom implementations can use their own valid raw cookie fields.

## Raw fields and compatibility

Each `Set-Cookie` field contains one cookie. Add fields separately rather than
combining them with commas; Expires dates themselves contain commas. Valid raw
fields retain their attributes, including SameSite and extensions not represented
by `System.Net.Cookie`. Callers own raw syntax and policy; the typed overload's
None/Secure validation does not rewrite arbitrary raw strings.

The managed response previously reparsed raw fields through CookieList and lost
unknown attributes. It also cleared raw response cookies during WebSocket
upgrade. Native cookie serialization could overwrite raw fields when ordinary
typed cookies were present. These paths now preserve independent response fields
and typed-cookie metadata. A far-future native expiration uses a date when its
age would exceed the range accepted by older CookieContainer parsers.

## Validation and limits

Real-listener tests cover both modes, raw fields and casing, unknown attributes,
Expires/Max-Age, mixed raw/typed cookies, long expiration, Lax/Strict/None,
validation without partial additions, mutable scopes, shared-cookie response
isolation, scoped duplicate names, session defaults/creation/deletion/configuration
locking, and ordinary/WebSocket responses. Existing cookie and session regressions
remain in the suite. The test-only Framework probe compiles for net472 and runs
against the Standard asset on the installed CLR; it does not certify an original
4.7.2 runtime or the reporters' exact applications/browsers.

Wire correctness is verified; browser acceptance under every privacy setting is
not claimed. The APIs remain unreleased until the owner requests publication.
