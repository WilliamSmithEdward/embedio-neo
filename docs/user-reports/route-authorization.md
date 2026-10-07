# Public reads and protected writes in one controller

This guide addresses [upstream #538](https://github.com/unosquare/embedio/issues/538):
allow anonymous `GET /data` while requiring authentication for `PUT /data` in the
same controller. Existing `OnBeforeHandler` runs after route resolution and
before request-data binding, so it can reject protected requests before `[JsonData]`
reads their bodies. No new library authorization attribute or API is required.

## Complete example

This is a loopback demonstration with deliberately public opaque tokens, **not a
production token service or JWT validator**. Real bearer credentials require HTTPS
and application-owned trusted validation. The program works with published
EmbedIO-Neo **1.0.2**.

```sh
dotnet new console --framework net10.0 -n RouteAuthorizationExample
cd RouteAuthorizationExample
dotnet add package EmbedIO-Neo --version 1.0.2
```

Replace `Program.cs` with this complete program:

```csharp
using System;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Threading;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var store = new DataStore();
using var server = new WebServer(o => o.WithUrlPrefix("http://localhost:8877/").WithMode(HttpListenerMode.EmbedIO))
    .WithWebApi("/api", api => api.WithController(() => new DataController(store)));
Console.WriteLine("GET /api/data is public. PUT requires a demo writer token. Press Ctrl+C to stop.");
await server.RunAsync(stop.Token);

public sealed class DataController : WebApiController
{
    private readonly DataStore _store;
    public DataController(DataStore store) => _store = store;

    protected override void OnBeforeHandler()
    {
        base.OnBeforeHandler(); // Retain the default no-cache headers.
        if (Request.HttpVerb == HttpVerbs.Get && Route.Path == "/data") return;

        var values = Request.Headers.GetValues(HttpHeaderNames.Authorization);
        if (values == null || values.Length == 0)
        {
            Response.Headers.Set(HttpHeaderNames.WWWAuthenticate, "Bearer realm=\"demo\"");
            throw HttpException.Unauthorized();
        }
        if (values.Length != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var header))
        {
            Response.Headers.Set(HttpHeaderNames.WWWAuthenticate, "Bearer realm=\"demo\", error=\"invalid_request\"");
            throw HttpException.BadRequest("One valid Authorization header is required.");
        }
        if (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            Response.Headers.Set(HttpHeaderNames.WWWAuthenticate, "Bearer realm=\"demo\"");
            throw HttpException.Unauthorized();
        }

        // DEMO ONLY: these public opaque tokens are not production credentials.
        IPrincipal? principal = header.Parameter switch
        {
            "demo-reader-token" => new GenericPrincipal(new GenericIdentity("demo-reader", "Bearer"), new[] { "data:read" }),
            "demo-writer-token" => new GenericPrincipal(new GenericIdentity("demo-writer", "Bearer"), new[] { "data:write" }),
            _ => null,
        };
        if (principal?.Identity?.IsAuthenticated != true)
        {
            Response.Headers.Set(HttpHeaderNames.WWWAuthenticate, "Bearer realm=\"demo\", error=\"invalid_token\"");
            throw HttpException.Unauthorized();
        }
        if (!principal.IsInRole("data:write"))
        {
            Response.Headers.Set(HttpHeaderNames.WWWAuthenticate, "Bearer realm=\"demo\", error=\"insufficient_scope\", scope=\"data:write\"");
            throw HttpException.Forbidden();
        }
    }

    [Route(HttpVerbs.Get, "/data")]
    public object GetData() => new { value = _store.Read() };

    [Route(HttpVerbs.Put, "/data")]
    public object PutData([JsonData] WriteInput input)
    {
        if (input == null || string.IsNullOrWhiteSpace(input.Value))
            throw HttpException.BadRequest("Value is required.");
        _store.Write(input.Value);
        return new { value = input.Value };
    }
}

public sealed class WriteInput
{
    public string? Value { get; set; }
}

public sealed class DataStore
{
    private readonly object _sync = new();
    private string _value = "initial";
    public string Read() { lock (_sync) return _value; }
    public void Write(string value) { lock (_sync) _value = value; }
}
```

Run `dotnet run`. The module prefix `/api` combines with the exact controller
route `/data`, so both methods use `http://localhost:8877/api/data`. Press Ctrl+C
to cancel and dispose the server. A fresh controller is created per request;
only the in-memory data store is shared. Its contents reset when the app restarts.

For the following shell requests, use `curl.exe` in a modern Windows shell:

```sh
curl -i http://localhost:8877/api/data
curl -i -X PUT http://localhost:8877/api/data -H "Content-Type: application/json" -d '{"value":"updated"}'
curl -i -X PUT http://localhost:8877/api/data -H "Authorization: Bearer demo-reader-token" -H "Content-Type: application/json" -d '{"value":"updated"}'
curl -i -X PUT http://localhost:8877/api/data -H "Authorization: Bearer demo-writer-token" -H "Content-Type: application/json" -d '{"value":"updated"}'
curl http://localhost:8877/api/data
```

Expected outcomes, in order:

| Request | Outcome |
| --- | --- |
| Anonymous GET | 200, `{"value":"initial"}` |
| PUT without credentials | 401 with a Bearer challenge; no write |
| PUT with authenticated reader | 403, insufficient write permission; no write |
| PUT with writer | 200, `{"value":"updated"}` |
| Next anonymous GET | 200, `{"value":"updated"}` |

In Windows shells with legacy native-argument quoting, use the built-in request
command for PUT instead (change/remove the header to exercise denial):

```ps1
Invoke-RestMethod http://localhost:8877/api/data -Method Put -ContentType application/json -Headers @{ Authorization = 'Bearer demo-writer-token' } -Body '{"value":"updated"}'
```

Authentication and authorization are separate. Missing/invalid credentials produce
401; a validated identity without the write permission produces 403. Merely having
a nonempty identity name is not proof of authentication. Here the identity and
permission are created only after a known demo token matches, and checked with
`IsAuthenticated` and `IsInRole`. The tokens are header-only and case-sensitive;
the Bearer scheme name is case-insensitive. Public GET deliberately ignores
credentials because it returns public data. The controller policy permits that
one resolved GET route; other resolved handlers require write permission rather
than implicitly becoming public when added later.

## Keep authorization ahead of binding

Do not consume the request body or call `.Result`/`.Wait()` in `OnBeforeHandler`.
The reported workaround reads the input before `[JsonData]` gets it, and the
body-reading helpers do not promise rewind/replay. That can leave the binder with
an exhausted or closed stream. Let `[JsonData]` perform its read after the header/
permission check; inspect the bound DTO in the handler if necessary. Avoid logging
credentials, full authorization headers or sensitive body fields.

For an authorized writer, malformed JSON, null data and a blank/missing `value`
return 400 without changing the store. For an unauthorized caller, authorization
should win even when the supplied body is malformed. Rejecting before application
binding is not a guarantee that the transport has received/buffered no body bytes;
enforce appropriate listener/body-size limits separately.

## Integrate a real authentication provider

Keep signature/issuer/audience/lifetime validation, revocation and token ownership
in a trusted application authentication component; decoding JWT payload fields
does not validate a token. Do not weaken the archived BearerTokenModule's rejection
logic to make protected routes pass. That legacy extras module is not included in
Neo's current packages, and this guide does not port it or claim JWT parity.

If validation is asynchronous, await it in an application-owned non-final module
registered before WebApiModule, and pass a validated principal through a private
key in `IHttpContext.Items`. That module must explicitly support the anonymous
route policy while retaining rejection for protected requests. The controller can
then perform synchronous authorization from that trusted request-local result.
Successful authentication middleware must allow dispatch to continue rather than
marking the request handled. Do not reuse controllers to retain a prior request's
principal. If a host/provider already supplies `HttpContext.User`, verify
`Identity.IsAuthenticated` and the actual required permissions; do not fabricate
authentication from a name, query parameter or client-supplied role header.

Use HTTPS for real bearer tokens; see [HTTPS hosting](../guides/https.md).
The demonstration's fixed tokens have no signing, expiration, revocation or
constant-time secret verification. Its permissive public GET is an application
choice, not a changed EmbedIO default. For production policy design, see
[RFC 6750](https://www.rfc-editor.org/rfc/rfc6750.html).

There is no built-in per-action authorization attribute introduced by this work.
Method-level checks are also possible, but parameter binding already occurs before
the method body; use the common hook or earlier middleware when denial must occur
before binding. Always call `base.OnBeforeHandler()` to retain its default caching
behavior. Custom authentication/authorization semantics remain application-owned.

## Validation

The complete program compiled against published 1.0.2. A temporary harness ran
the program against current source and the exact published assembly, with timed
cancellation as its only source change: 35 HTTP checks
covered anonymous reads, credentials/scheme/token casing, reader versus writer
permissions, rejection before malformed body binding, null/missing/blank input,
Unicode writes, no mutation after denial, later anonymous requests, query-token
rejection, exact-route misses and an unauthorized incomplete-body request.
The incomplete request received 401 without sending the declared body, and a
fresh GET remained healthy. A separate controlled body-pre-read example returned
400 in both listener modes. These results do not establish the original app's
exact failure or certify a production authentication provider.
