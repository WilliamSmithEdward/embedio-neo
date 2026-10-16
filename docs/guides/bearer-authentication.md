# Bearer authentication and controller authorization

These opt-in APIs are under development after EmbedIO-Neo 2.1.0. Existing Basic
authentication and application authorization hooks retain their behavior.

`BearerAuthenticationModule` processes one `Authorization: Bearer ...` header and
awaits an application-owned validator. The validator returns an authenticated
`ClaimsPrincipal`, or null for an invalid credential. Register the module before
the endpoints that use its identity. Each request starts with an anonymous user;
successful validation publishes cloned authenticated identities for that request.
Unauthenticated identities returned alongside an authenticated identity contribute
neither claims nor roles. Token values are never logged by this module.

The default module permits missing credentials so public and protected actions
can coexist. Invalid supplied bearer credentials still fail on public actions.
Set `RequireAuthentication = true` to protect the entire module path. An
`[AllowAnonymous]` attribute cannot bypass that upstream requirement, Basic
authentication, or another module's access check.

Use HTTPS for real bearer credentials. Anyone possessing a bearer token can use
it. Tokens in query strings or form bodies are not accepted by this module.
The syntax and challenge behavior follow [RFC 6750](https://www.rfc-editor.org/rfc/rfc6750).

## A complete controller example

Build this program against the source containing the feature. It uses public,
synthetic loopback credentials to demonstrate configuration; replace the validator
with trusted application validation and configure HTTPS for deployment.

```csharp
using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.Authorization;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };

var api = new WebApiModule("/api");
api.ConfigureAuthorization(new AuthorizationOptions().AddPolicy("write", (context, token) =>
{
    token.ThrowIfCancellationRequested();
    return Task.FromResult(context.User is ClaimsPrincipal claims && claims.HasClaim("permission", "write"));
}));
api.RegisterController<FilesController>();

using var server = new WebServer("http://localhost:9696/");
server.WithModule(new BearerAuthenticationModule("/api", ValidateAsync));
server.WithModule(api);
try { await server.RunAsync(stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

static Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken cancellation)
{
    cancellation.ThrowIfCancellationRequested();
    if (token != "demo-editor") return Task.FromResult<ClaimsPrincipal?>(null);
    var identity = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.Name, "editor"),
        new Claim(ClaimTypes.Role, "editor"),
        new Claim("permission", "write"),
    }, "Bearer");
    return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
}

[Authorize]
public sealed class FilesController : WebApiController
{
    [AllowAnonymous]
    [Route(HttpVerbs.Get, "/status")]
    public object Status() => new { ready = true };

    [Authorize("write", Roles = "editor,administrator")]
    [Route(HttpVerbs.Post, "/save")]
    public object Save([JsonData] string value) => new { savedBy = User?.Identity?.Name, value };
}
```

`GET /api/status` permits missing credentials. `POST /api/save` requires an
authenticated user, the `write` policy and either listed role. Send
`Authorization: Bearer demo-editor` and a JSON string body to exercise it.
Authorization runs after route selection and before controller activation,
preprocessing and parameter binding, including JSON and multipart body readers.
This does not promise that the network transport never receives or drains bytes
from a rejected request.

## Combining requirements

- Controller and method attributes, including inherited attributes, combine with
  AND. An overriding method inherits applicable base-method attributes; a distinct
  hidden method does not inherit another method's requirements.
- Roles within one attribute combine with OR. Multiple attributes can express
  multiple required role groups. Role names are case-sensitive.
- A named policy is an asynchronous callback that returns true to permit access.
  Authentication is required before any policy runs. Callbacks may inspect claims,
  resources and application services; they must honor cancellation and support
  concurrent requests.
- `[AllowAnonymous]` overrides controller/action authorization attributes.
- Configure policies before registering controllers. Unknown policy names fail
  registration. Configuration is copied, so later additions to the original
  options object do not change registered controllers.

Generated bindings carry the same immutable requirements as reflection-based
registration, including inheritance. Generated authorization metadata uses no
runtime attribute reflection and is covered by the Native AOT smoke program.
The existing controller activation and disposal contracts are retained.

## Responses and cancellation

| Condition | Result |
| --- | --- |
| Missing credentials on an optional module/public action | Anonymous request continues |
| Missing credentials on a protected action or required module | 401 and a Bearer challenge |
| Malformed, duplicate or oversized bearer credentials | 400 and `invalid_request` |
| Well-formed token rejected by the validator | 401 and `invalid_token` |
| Authenticated user fails a role or policy | 403 |
| Unexpected validator/policy failure | Normal application error handling; access is not granted |

`MaximumTokenLength` defaults to 16,384 ASCII characters and is checked before
validation. `Realm` optionally labels challenges and safely quotes printable ASCII.
Successful requests do not receive a challenge header.

The module checks context cancellation before validation and before publishing the
identity. Policies are checked before and after awaiting their callback. Context
cancellation follows the server's existing contract; a remote disconnect does not
automatically cancel every application callback. Callbacks own any additional
timeouts or external I/O they start. Cryptographic validation cannot be forcibly
interrupted halfway through an operation.

## Optional JWT package

`EmbedIO-Neo.Jwt` supplies `JwtBearerTokenValidator` using pinned Microsoft
IdentityModel validation. Core authentication does not depend on IdentityModel.
The adapter accepts signed, compact JWTs with explicit issuer, audience, trusted
keys and signing-algorithm allowlist. It requires expiration and validates lifetime;
it does not issue tokens or perform remote discovery. JWE is not supported.

```csharp
using System.Security.Cryptography;
using EmbedIO.Authentication;
using EmbedIO.Jwt;
using Microsoft.IdentityModel.Tokens;

// Obtain this verification key through trusted deployment configuration.
using var rsa = RSA.Create();
rsa.ImportFromPem(System.IO.File.ReadAllText("trusted-signing-public-key.pem"));
var options = new JwtBearerOptions
{
    Issuer = "https://identity.example.com",
    Audience = "embedio-api",
};
options.SigningKeys.Add(new RsaSecurityKey(rsa));
options.AllowedAlgorithms.Add(SecurityAlgorithms.RsaSha256);
var validator = new JwtBearerTokenValidator(options);
var authentication = new BearerAuthenticationModule("/api", validator.ValidateAsync);
// Register authentication before the protected WebApiModule.
```

RSA, EC and symmetric keys are supported; symmetric keys must remain secret.
Supported verification material and options are copied at construction, so later
configuration mutations or disposal of the original asymmetric key do not change
trust. Create and install a new validator when rotating keys. Algorithms must be
explicitly permitted; never derive trust from the incoming token's algorithm or
untrusted key locations. Issuer and audience are exact, including audience trailing
slashes. Clock skew defaults to zero and can be configured explicitly. Original
claim names are retained; name and role claim types default to `sub` and `role`.
Validation errors return no principal, without returning token details to clients.

## OpenAPI and fuzzing

Call `OpenApiDocumentBuilder.AddBearerAuthentication("bearer", authentication)` to
describe this module. Controller requirements contribute Bearer security and
401/403 responses; role and policy names are descriptive metadata, not OAuth
scopes. Explicit upstream authentication requirements remain documented on
anonymous controller actions when they still apply. See [OpenAPI](openapi.md).

The [state-machine campaigns](multi-connection-fuzzing.md) include `auth-state`,
`auth-jwt`, `auth-http1` and `auth-http2`. They compare generated identities and access decisions
with independent request ledgers, exercise overlapping validators and reused
connections, and verify that rejected requests do not invoke protected handlers.
The in-process campaign also observes application body binding and cancellation.
Network cancellation tests do not assume that canceling a client cancels a server
validator. Replay traces and failure evidence retain the existing bounded cleanup
and invariant-preserving shrinking behavior.
