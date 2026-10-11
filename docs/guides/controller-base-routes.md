# Controller base routes and catch-all handling

[Upstream #432](https://github.com/unosquare/embedio/issues/432) asked how to serve
`/someurl`, `/someurl/1` and arbitrary deeper paths without catching 404 exceptions.
The later upstream `BaseRouteAttribute` implementation already supports this in
EmbedIO-Neo. This guide verifies existing behavior; it introduces no new routing API.

## A complete server

Create a .NET 10 console application and reference the core package, or the core
project in this checkout. Keep the following program in `Program.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

var prefix = args.Length == 0 ? "http://localhost:9696/" : args[0];
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};
using var server = new WebServer(HttpListenerMode.EmbedIO, prefix)
    .WithWebApi("/someurl", api => api
        .WithController<KnownController>()
        .WithController<ParameterizedController>()
        .WithController<CatchAllController>());
await server.RunAsync(stop.Token);

public sealed class KnownController : WebApiController
{
    [Route(HttpVerbs.Get, "/health")]
    public string Health() => "healthy";
}

public sealed class ParameterizedController : WebApiController
{
    [BaseRoute(HttpVerbs.Get, "/other/{id}/")]
    public string Get(int id) => $"{id}|{Route.SubPath}";
}

public sealed class CatchAllController : WebApiController
{
    [BaseRoute(HttpVerbs.Any, "/")]
    public string? Handle() => Route.SubPath;
}
```

The default serializer returns JSON strings. For example:

| Request | JSON response |
| --- | --- |
| `GET /someurl/health` | `"healthy"` |
| `GET /someurl` or `/someurl/` | `"/"` |
| `GET /someurl/1/2` | `"/1/2"` |
| `GET /someurl/other/42/child` | `"42|/child"` |
| `POST /someurl/arbitrary/path` | `"/arbitrary/path"` |
| `GET /else/1` | HTTP 404 |

`Route.SubPath` includes a leading slash. It is relative to the matched controller
base route, after the Web API module mount has been removed. It excludes query
parameters; read those through `Request.QueryString`. Captured prefix parameters
still bind normally, and invalid integer values retain the normal HTTP 400 response.

## Scope and fallback order

Register specific controllers before the catch-all controller. Resolvers are tried
in registration order: a root base route registered first can handle `/health`
before the specific route. Do not rely on reflection order between methods in one
controller to establish fallback priority.

The catch-all is scoped to `/someurl`; it does not intercept unrelated module
mounts. This is an ordinary handler for otherwise unmatched paths in that module,
rather than catching an exception after another handler returns 404.

`HttpVerbs.Any` deliberately handles other HTTP methods too, including paths with
a GET-only specific route. Use `HttpVerbs.Get` instead when the base route should
accept only GET. Without an Any fallback, a matching GET-only base route retains
the normal HTTP 405 behavior for POST.

## Wildcards and custom regex

`[Route(HttpVerbs.Any, "/*")]` does not mean every descendant: `*` is literal
route text. On the tested source it handles `/someurl/*`, not `/someurl/1/2`.
Route definitions use EmbedIO's route grammar. Regex matching inside the resolver
does not make arbitrary regex strings a public route-definition API; characters
such as `.` and `+` remain literal.

For application-specific matching, use a scoped base-route handler and examine
`Route.SubPath` in application code. An application-owned parser or bounded regex
can make that decision without changing the library's route grammar. No arbitrary
regex route API, wildcard syntax change or automatic specificity sorting is added
by this work.

## Validation and attribution

`Issue432_BaseRoutes` covers the original literal-star control, arbitrary descendants,
parameterized prefixes, invalid binding, module boundaries, registration-order
fallbacks, HTTP verbs, literal regex metacharacters and preserved query/URL data.
These 23 cases run through TestWebServer's in-process HTTP/module pipeline; that
does not claim native-listener or original-2020-application reproduction. The exact\nprogram above also compiled without warnings and passed six independent curl\nchecks against the real managed HTTP listener in a pinned Linux/.NET 10.0.12\ncontainer, including the scoped outside-path 404. No native listener was claimed\ntested by that program.

Thanks to Temtaime and k3z0 for describing the use cases, and to Giovanni Perez
and rdeago for the original discussion and base-route design. EmbedIO-Neo continues
the archived EmbedIO project while preserving its authors' credit.