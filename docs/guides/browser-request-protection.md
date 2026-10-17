# Browser request protection

Browser request protection requires EmbedIO-Neo 2.7.0 or later.

`BrowserRequestProtection` adds opt-in host/origin validation and session-bound
anti-forgery tokens. Existing applications retain their defaults. This is separate
from authentication and authorization: a token proves possession of a value issued
for the current session/identity, not permission to perform an action.

## A complete same-origin browser application

Use EmbedIO-Neo 2.7.0 or later, target `net10.0`, and reference `EmbedIO-Neo`. Put this in
`Program.cs`. Put the HTML below in `wwwroot/index.html` relative to the working
directory. The example deliberately binds only `127.0.0.1:9696`; open that exact URL.
The in-memory counter demonstrates a mutation, not durable application storage.

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Security;
using EmbedIO.Sessions;
using EmbedIO.Routing;
using EmbedIO.WebApi;

const string url = "http://127.0.0.1:9696/";
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var protection = new BrowserRequestProtection(
    new BrowserRequestProtectionOptions(new[] { url }));
var api = new WebApiModule("/api", ResponseSerializer.None(false));
api.ConfigureBrowserProtection(protection);
api.WithController(BrowserBindings.BrowserApi, () => new BrowserApi(protection));
var root = Path.GetFullPath("wwwroot");
Directory.CreateDirectory(root);
using var server = new WebServer(options => options.WithUrlPrefix(url)
        .WithMaximumRequestBodyBytes(32_768)
        .WithRequestTimeout(TimeSpan.FromSeconds(15))
        .WithMaximumConcurrentRequests(64))
    .WithSessionManager(new LocalSessionManager
    {
        CookieHttpOnly = true,
        CookieSameSite = CookieSameSiteMode.Strict,
    })
    .WithModule(api)
    .WithStaticFolder("/", root, false);
Console.WriteLine(url);
try { await server.RunAsync(stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

[GeneratedControllerBinding(typeof(BrowserApi))]
internal static partial class BrowserBindings { }

[BrowserProtection]
public sealed class BrowserApi : WebApiController
{
    private readonly BrowserRequestProtection _protection;
    private static int _changes;
    public BrowserApi(BrowserRequestProtection protection) => _protection = protection;

    [Route(HttpVerbs.Get, "/token")]
    public string Token() => _protection.GetToken(HttpContext);

    [Route(HttpVerbs.Post, "/change")]
    public async Task<string> Change()
    {
        var text = await HttpContext.GetRequestBodyAsStringAsync();
        var number = Interlocked.Increment(ref _changes);
        return "Change " + number.ToString(CultureInfo.InvariantCulture) + ": " + text;
    }
}
```

```html
<!doctype html>
<html lang="en">
<meta charset="utf-8">
<title>Protected local API</title>
<button id="change" disabled>Submit change</button>
<pre id="result"></pre>
<script type="module">
const button = document.querySelector('#change');
const output = document.querySelector('#result');
async function token() {
  const response = await fetch('/api/token', {
    credentials: 'same-origin', cache: 'no-store'
  });
  if (!response.ok) throw new Error(`Token request failed: ${response.status}`);
  return response.text();
}
let csrf = await token();
button.disabled = false;
button.addEventListener('click', async () => {
  button.disabled = true;
  try {
    const response = await fetch('/api/change', {
      method: 'POST', credentials: 'same-origin',
      headers: { 'X-CSRF-Token': csrf, 'Content-Type': 'text/plain' },
      body: 'hello'
    });
    output.textContent = `${response.status}: ${await response.text()}`;
    // A rejection can have several causes. Refresh for the next user action;
    // do not automatically retry a mutation whose outcome is uncertain.
    if (response.status === 403) csrf = await token();
  } catch (error) {
    output.textContent = String(error);
  } finally { button.disabled = false; }
});
</script>
</html>
```

The first GET creates/resolves the session and returns a new 119-character token
with `Cache-Control: no-store`. The browser retains the HTTP-only session cookie
and sends the token in `X-CSRF-Token` when changing data. Tokens are never searched
for in URLs, cookies or request bodies. Ordinary HTML form submissions therefore
need JavaScript to add the header. Do not log tokens or insert them into URLs.

Authentication middleware must run before the protected API and token issuance.
Apply existing authorization attributes/policies to both endpoints as appropriate.
After login/logout, regenerate the session, then fetch a new token for the new
identity. Session regeneration now resolves a cookie-backed session before deleting
it, even if this request has not accessed session data yet. Old cookies must not
select the replaced session. Coordinate application mutations already in flight;
regeneration does not roll them back or revoke contexts that already hold a session.

## Exact target and browser origins

`serverOrigins` names physical request URL origins, including scheme and port.
`requestOrigins` names allowed browser origins and defaults to `serverOrigins`.
Each list contains one to 64 entries and is copied. Wildcards, suffix matches,
credentials, paths, query strings, fragments, opaque `null` origins, non-ASCII
hosts and trailing-dot aliases are rejected. Use ASCII IDNA host names when needed.
Configuration accepts a trailing slash; an Origin header must contain only the
origin. Host casing and default ports normalize through `Uri`.

Every checked request must target an allowed server origin. A supplied Host header
must be singular and agree with the request URL. Every supplied Origin must be one
singular allowed origin; duplicates and comma/space lists are rejected. No request
header can extend the allow-lists. This gives an explicitly configured loopback
application a host/origin boundary against browser requests to an untrusted host.
It does not turn a loopback listener into an authentication boundary against native
programs, extensions or malware that can set their own headers.

GET, HEAD and OPTIONS need no token. They must not mutate application state.
Other methods require one valid token by default, including login/logout endpoints.
A missing Origin on an ordinary request is allowed subject to that token rule;
`Referer` and Fetch Metadata are not used as alternative credentials. Supplied bad
origins are rejected even when a valid token is present.

Forwarded headers are not consulted. Behind a reverse proxy, explicitly configure
the physical URL origins seen by this pipeline and the external browser origins;
configure the proxy's Host behavior and existing trusted-forwarding policy separately.
No wildcard or automatic trust of X-Forwarded-Host/Proto is introduced.

## Controller policies and middleware

Call `ConfigureBrowserProtection(protection)` before registering controllers.
Every route in that module then uses the policy, including unannotated routes and
old generated bindings. Without configuration, unannotated routes are unchanged;
attributes without configuration fail registration. The wrapper runs after existing
authorization and before controller activation, hooks, body binding and actions.
Generated and reflection bindings have the same behavior, including Native AOT.

A method attribute overrides the class attribute, including inherited declarations:

```csharp
[BrowserProtection(BrowserProtectionMode.IgnoreToken)]
public abstract class NativeApi : WebApiController
{
    [Route(HttpVerbs.Post, "/browser-operation"), BrowserProtection]
    public void BrowserOperation() { /* Requires a token again. */ }
}
```

`IgnoreToken` exempts only the token requirement. Target checks, supplied Origin
checks and mandatory WebSocket Origin checks remain. Reserve exemptions for routes
with independently enforced non-browser authentication, for example an application
policy that requires validated bearer authentication. The mere presence of an
Authorization header never causes an exemption. `AllowAnonymous` and before-handler
`Skip` do not bypass browser protection. Existing int/status APIs and ordinary
serialization defaults are unaffected.

For non-controller handlers and WebSockets, register
`new BrowserRequestProtectionModule(baseRoute, protection)` after authentication
and before handlers or CORS middleware that could short-circuit the pipeline.
Its optional `requireToken: false` explicitly selects host/origin checks without
token checks. Controller attributes cannot undo an earlier middleware requirement.
The module and controller module do not dispose the shared policy.

WebSocket handshakes require an allowed Origin, including GET and extended CONNECT.
They use normal application authentication rather than a custom CSRF header, which
native browser WebSocket APIs cannot set. A native client without Origin is rejected
by this opt-in policy; explicitly send an allowed Origin and authenticate it. Origin
is a browser constraint, not proof that a native program is trusted.

OpenAPI authentication requirements remain separate; this policy does not automatically
add an API-key scheme or document a module's default token requirement. Describe the
conditional unsafe-method header requirement in your application API documentation.

## Tokens, lifetime and bounds

The policy owns a random 256-bit signing key and uses HMAC-SHA256 with distinct
signing/binding domains. The token contains a version, issuance time, random nonce,
authenticated opaque session/identity binding and signature. Session IDs and user
names are not emitted. Canonical encoding, version, signature and age are checked
before session access. Equal-length MACs use fixed-time comparison on .NET 10 and
a fixed-iteration comparison on .NET Standard 2.0.

The default lifetime is 20 minutes; configure a positive value at most one day.
Tokens are reusable until expiry, so several tabs can use separate tokens without
invalidating each other. UTC clock rollback rejects future-issued tokens; clock
advance can expire them. A new/disposed policy, expired/replaced session, or changed
authenticated identity rejects old tokens. The binding includes authentication
type and name, plus the type, value, issuer and original issuer of each authenticated identity's configured name claim and
NameIdentifier claim. Equal names/subjects from different issuing authorities do
not share a binding. Other claim changes are not bound; regenerate sessions when
changing accounts. The binding permits at most 16 identities and
1,024 UTF-16 characters per bound string. Role/policy decisions are still evaluated
for each request; this does not cache authorization.

There is no per-token dictionary, eviction timer or replay registry. Issuance retains
one marker in the existing session so the built-in manager does not purge it as empty.
The session store's number of sessions remains application-owned; use the existing
request admission/rate limits and suitable session storage for your exposure. A valid
issued token on an unknown cookie can resolve a fresh session and is then rejected
for a binding mismatch. Structurally forged or expired tokens do not create sessions.

Own one policy for a deliberate application/security scope, share it with issuance
and all consumers, stop its users, then dispose it. Dispose erases the owned key.
Restart/replacement invalidates tokens. The key is not persisted or shared between
processes; this is not a distributed anti-forgery service. No authentication, TLS,
CORS, cookie or listener settings are enabled automatically. Use HTTPS and secure
cookies for non-loopback deployment, with a compatible SameSite policy.

Protection cannot repair XSS, an overly permissive trusted UI, token leakage,
mutations in safe methods, or application authorization mistakes. See
[OWASP CSRF prevention](https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html)
and [bearer authentication](bearer-authentication.md).

## Validation and fuzzing

Focused tests cover controller parity, inherited overrides, token/cookie isolation,
identity/issuer changes, expiry, duplicate/noncanonical headers, no-store issuance and
session regeneration. Real-listener tests exercise HTTP/1, HTTP/2, HTTP/3 and the
HTTP/1, HTTP/2 and HTTP/3 WebSocket origin boundaries. Native AOT and the legacy-target smoke include
browser protection; configured jobs alone are not evidence of a passing run.

The `browser-protection` state-machine campaign overlaps three client requests at
a barrier and independently models issued cookies, identities and tokens. It checks
admission, action side effects and response isolation through session replacement,
cookie sharing, token tampering, foreign policies and origin/host changes. The
`--browser-protection-selftest` must reject a deliberately corrupted response.
A minimized empty-cookie-copy model failure is retained in the checked trace.
See [stateful surface fuzzing](stateful-surface-fuzzing.md).
