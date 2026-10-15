# EmbedIO-Neo primer for AI agents

This is a primer for an AI agent that builds an application on EmbedIO-Neo v2.
It states the rules and APIs an agent needs to write correct code the first
time, and links to the guides that hold the detail. It is not a contributor
guide; see [CONTRIBUTING.md](CONTRIBUTING.md) for changing the library itself.

Every API named here exists in the v2 source. When this primer and a guide
disagree, the guide and the source win.

## 1. What you are building on

| Item | Value |
| --- | --- |
| NuGet package | `EmbedIO-Neo` (DI integration: `EmbedIO-Neo.DependencyInjection`) |
| Assembly and namespaces | `EmbedIO`, `EmbedIO.WebApi`, `EmbedIO.Routing`, `EmbedIO.WebSockets`, `EmbedIO.Files`, `EmbedIO.Security`, `EmbedIO.Net` and others |
| Targets | `net10.0` and `netstandard2.0` |
| Current version | 2.0.0, the v2 engine. 1.0.x is the previous major version. |
| Install | `dotnet add package EmbedIO-Neo` |

Check which version the project references before you write code. A 1.0.x
package lacks the v2 engine, HTTP/2, HTTP/3 and every API the guides mark as
new in Neo v2.

The `net10.0` asset is the full product. The `netstandard2.0` asset has no
HTTP/3, no HTTP/2 over TLS (no ALPN), no telemetry, no Brotli and no TLS
certificate selector. Target `net10.0` unless you must support an old runtime.

## 2. A complete working server

This program runs as written. It serves a JSON API through a controller, a
WebSocket echo endpoint and a static folder, with explicit request limits.

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
Directory.CreateDirectory(webRoot);

using var server = new WebServer(options => options
        .WithUrlPrefix("http://localhost:9696/")
        .WithMaximumRequestBodyBytes(1_048_576)
        .WithRequestTimeout(TimeSpan.FromSeconds(30))
        .WithMaximumConcurrentRequests(256))
    .WithWebApi("/api", api => api.WithController<ItemsController>())
    .WithModule(new EchoSocket("/ws"))
    .WithStaticFolder("/", webRoot, true);

try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

public sealed class ItemsController : WebApiController
{
    [Route(HttpVerbs.Get, "/items/{id}")]
    public object GetItem(int id) => new { id };

    [Route(HttpVerbs.Post, "/items")]
    public object CreateItem([JsonData] CreateItemInput input)
    {
        if (string.IsNullOrWhiteSpace(input?.Name))
            throw HttpException.BadRequest("Name is required.");

        HttpContext.Response.StatusCode = 201;
        return new { name = input.Name };
    }
}

public sealed class CreateItemInput
{
    public string? Name { get; set; }
}

public sealed class EchoSocket : WebSocketMessageModule
{
    public EchoSocket(string path)
        : base(path)
    {
        MaxMessageSize = 65_536;
    }

    protected override Task OnTextMessageReceivedAsync(IWebSocketContext context, string text)
        => SendAsync(context, text);
}
```

Try it: `GET http://localhost:9696/api/items/42` returns `{"id":42}`.

## 3. Server construction and lifetime

- Build with `new WebServer(options => options.WithUrlPrefix(...)...)` or
  `new WebServer("http://localhost:9696/")`. The listener is created in the
  constructor, so listener and TLS configuration errors throw there.
- Configure everything before starting. Options and modules lock when the
  server starts; setting them later throws `InvalidOperationException`.
- `RunAsync(token)` starts the server and completes when it stops. Call it once.
  A stopped server cannot be restarted; create a new instance.
- Stop by cancelling the `RunAsync` token or disposing the server. Open
  WebSockets get a 1001 close and the server waits at most one second for them.
  There is no `StopAsync`.
- `DrainAsync(timeout)` stops accepting work and lets admitted requests finish.
  `DrainStarted` is a token cancelled when a drain begins.
- `server.Start()` runs `RunAsync` in the background and returns once listening.
  Startup failures then surface only through the background task, so prefer
  awaiting `RunAsync`.
- `server.StateChanged` and `server.State` report Created, Loading, Listening
  and Stopped.

## 4. Choosing the protocol

Select with `options.WithMode(HttpListenerMode.X)`:

| Mode | Serves | Requirements |
| --- | --- | --- |
| `EmbedIO` (default) | HTTP/1.1 and HTTP/2 over TCP | HTTP/2 needs TLS with ALPN (`net10.0` asset) or cleartext prior knowledge |
| `EmbedIOHttp3` | HTTP/3 over QUIC/UDP only | `net10.0`, HTTPS prefixes, a certificate with its private key, native QUIC (MsQuic), TLS 1.3 |
| `EmbedIOCombined` | Both of the above on the same HTTPS prefixes | Everything HTTP/3 needs; both bindings must succeed |

- HTTP/2 needs no opt-in in the default mode.
- `HttpListenerMode` value 1 (the old Microsoft listener) is removed and throws.
- Unsupported HTTP/3 platforms throw `PlatformNotSupportedException` at startup.
- There is no automatic Alt-Svc advertisement; clients must be told HTTP/3 is
  available. See the [HTTP/3 guide](docs/guides/http3.md).

## 5. HTTPS and TLS

```text
options.WithUrlPrefix("https://*:8443/").WithCertificate(certificate)
```

- An `https` prefix without a certificate or certificate source throws before
  the server starts. The certificate needs its private key.
- TLS 1.2 is the minimum; TLS 1.0 and 1.1 clients cannot connect.
- Rotate certificates with `EmbedIO.Net.ServerCertificateSource`: pass it to
  `WithCertificateSource(source)` and call `source.Publish(newCertificate)`.
  Configure a source or a fixed certificate, not both.
- Client certificates, protocol versions and SNI selection go in
  `EmbedIO.Net.ServerTlsPolicy` through `WithTlsPolicy(policy)`.
- Two servers sharing one TCP endpoint must present the same certificate and TLS
  policy, or startup throws. HTTP and HTTPS cannot share an endpoint.
- The store helpers (`WithCertificateThumbprint`, `WithAutoLoadCertificate`) are
  Windows-only.

Guides: [HTTPS](docs/guides/https.md), [TLS policy](docs/guides/tls-policy.md),
[certificate rotation](docs/guides/certificate-rotation.md).

## 6. Modules and request flow

A server is a pipeline of modules. Order is the single most common source of
bugs.

- Modules run in registration order. Each is skipped unless the request path is
  under its base route. Dispatch stops at the first module that handles the
  request. If none does, the response is 404.
- Final handlers end dispatch: `WithWebApi`, `WithStaticFolder` (and the other
  file modules) and WebSocket modules.
- Middleware does not end dispatch unless it rejects the request:
  `WithCors`, `WithBasicAuthentication`, `WithIPBanning`, forwarded headers and
  rate limiting.
- Register middleware first, then APIs and WebSockets, then specific file
  mounts, then any catch-all static folder at `/` last.
- `OnGet("/x", handler)` and the other `On*` shortcuts match a base path,
  including everything beneath it. Use a controller for exact routes.
- Module registration: `WithModule(module)`, `WithWebApi(baseRoute, configure)`,
  `WithStaticFolder(baseRoute, path, isImmutable)`, `WithEmbeddedResources`,
  `WithZipFile`, `WithCors`, `WithBasicAuthentication`, `WithIPBanning`,
  `WithLocalSessionManager`.
- Modules with no `With*` helper (rate limiting, forwarded headers, client
  banning) are added with `WithModule(new ...Module(...))`.

## 7. Routing and Web API controllers

- Derive from `WebApiController`. Mark methods with
  `[Route(HttpVerbs.Get, "/items/{id}")]`. The module's base route is prefixed:
  `WithWebApi("/api", ...)` plus `/items/{id}` serves `/api/items/{id}`.
- `{id}` requires a non-empty segment; `{id?}` allows an empty one. Query strings
  are not part of route matching.
- `[BaseRoute(verb, "/files/")]` matches a prefix; the remainder is in
  `Route.SubPath`. A base route must end with `/`; a normal route must not.
- Parameter binding: a binding attribute, else the route parameter with the same
  name (converted to the parameter type), else the parameter default.
  - `[QueryField]`, `[QueryField(true)]` (required, 400 if missing)
  - `[FormField]`, `[FormData]`, `[QueryData]`
  - `[JsonData]` deserializes the body; invalid JSON is a 400
  - `[FromServices]` (DI package)
- Return an object to send JSON with status 200. Return `Task<T>` for async work.
  Return `void` or `Task` for no body. `ValueTask` is not awaited; do not use it.
- Set `HttpContext.Response.StatusCode` before returning to change the status.
- Throw `HttpException.BadRequest(message)`, `NotFound()`, `Unauthorized()`,
  `Forbidden()` or `MethodNotAllowed()` for HTTP errors.
- Override `OnBeforeHandler()` for per-controller checks before binding, such as
  authorization. It runs before the body is read.
- Controllers need a public parameterless constructor for
  `WithController<T>()`, or use `WithController<T>(() => new T(...))`.
- `CaseInsensitiveRoutes` on the Web API module enables case-insensitive literal
  route text. Set it before registering controllers.

Guides: [controllers](docs/guides/getting-started/controllers.md),
[routes and parameters](docs/guides/getting-started/requests.md),
[base routes](docs/guides/controller-base-routes.md).

## 8. Reading requests

- Helpers on the context: `GetRequestBodyAsStringAsync()`,
  `GetRequestBodyAsByteArrayAsync()`, `GetRequestDataAsync<T>()` (JSON),
  `GetRequestFormDataAsync()` and `GetRequestQueryData()`.
- **Read the body once and keep the result.** The helpers dispose the request
  stream. A second read fails or returns nothing, depending on protocol and
  version. Do not mix `[JsonData]` with a manual body read in the same request.
- A body can have unknown length: chunked uploads are decoded and
  `ContentLength64` can be -1. Read to the end instead of trusting the length.
- Compressed request bodies are decoded only when `WithSupportCompressedRequests(true)`
  is set. If you enable it, also set a finite decompressed-size limit.
- Read the body before the response completes, and never read the same request
  stream from two tasks at once.

## 9. Writing responses

- `SendDataAsync(obj)` writes JSON. `SendStringAsync(text, contentType, encoding)`
  requires an explicit encoding; use `WebServer.Utf8NoBomEncoding`.
- `Redirect(location, statusCode)` accepts only 3xx statuses.
- Set status, headers and cookies before the first body write or flush. After
  headers are sent they cannot change.
- Response text is validated. `StatusDescription` with a control character throws
  `ArgumentException`; a header or cookie value with a control character throws
  `InvalidOperationException` when the response is written. Never copy request
  input into headers without validating it.
- `StatusCode` must be 200-599. Send 1xx through `SendInformationalAsync`; see
  [response field sections](docs/guides/response-field-sections.md).
- Cookies: `Response.SetCookie(cookie)` or `SetCookie(cookie, CookieSameSiteMode.Lax)`.
  `CookieSameSiteMode.None` requires a Secure cookie.
- Dynamic responses are compressed automatically when the client accepts it. On
  `net10.0`, Brotli wins ties with gzip.
- Server-sent events: `var events = context.OpenEventStream();` then
  `events.WriteAsync(data, eventName)`. See [progress events](docs/user-reports/progress-events.md).
- An unhandled exception becomes an empty 500. Use
  `server.WithProblemDetails()` (namespace `EmbedIO.Problems`) for RFC 9457
  JSON errors. See [problem details](docs/guides/problem-details.md).

## 10. JSON

- The serializer is `EmbedIO.Serialization.Json` over System.Text.Json, with
  compatibility settings: case-insensitive names, public fields included,
  quoted numbers, trailing commas, and properties with private or protected
  setters bound.
- **Over-posting.** Because public fields and non-public setters are bound,
  a request body can set any of them. Never deserialize request JSON straight
  into a domain or persistence type. Use a dedicated input type that contains
  only client-settable members, or mark the rest `[JsonIgnore]`.
- Customize: `WithWebApi("/api", ResponseSerializer.Json(options), ...)` for
  responses; `GetRequestDataAsync(RequestDeserializer.Json<T>(options))` for
  requests. `[JsonData]` always uses the library defaults.
- A fresh `new JsonSerializerOptions()` is strict standard behavior, not the
  compatibility behavior.

Guides: [JSON compatibility](docs/user-reports/json-migration-compatibility.md) and [over-posting](docs/user-reports/json-migration-compatibility.md#over-posting-and-the-binding-surface).

## 11. WebSockets

- Derive from `WebSocketMessageModule(path)` and override
  `OnTextMessageReceivedAsync` and/or `OnBinaryMessageReceivedAsync`. Messages are
  complete, delivered one at a time per connection, and text is strict UTF-8.
  Unhandled message types close with 1003, invalid text 1007, oversize 1009, a
  callback failure 1011.
- The lower-level `WebSocketModule` delivers raw frames to
  `OnMessageReceivedAsync`.
- The endpoint path matches exactly; deeper paths pass through.
- `SendAsync` and `BroadcastAsync` (protected) send to clients; failures are
  logged, not thrown.
- Set `MaxMessageSize` in the constructor. Check the
  [request-limits guide](docs/guides/request-limits.md) for its default.
- `KeepAliveInterval` (30 s) pings clients and closes those that do not answer.
  `TimeSpan.Zero` turns it off.
- `IWebSocket.CloseAsync` throws `ArgumentException` for close codes a server
  cannot send (below 1000, 1006, 1010, 1015-2999, 5000 and above) and for
  comments over 123 UTF-8 bytes.
- `IWebSocket.SendAsync` throws `WebSocketException` if the socket closes before
  the whole message is sent.
- Messages a client sends right after connecting are queued until
  `OnClientConnectedAsync` completes. For server-to-client sends right after
  connect, have the client send a readiness message first.
- WebSockets work over HTTP/1.1, and over HTTP/2 and HTTP/3 through extended
  CONNECT.

Guides: [startup messages](docs/user-reports/websocket-startup-messages.md),
[handshake status](docs/guides/websocket-handshake-status.md),
[receive limits](docs/guides/websocket-receive-limits.md).

## 12. Static files

- `WithStaticFolder("/", path, isImmutable)` serves a directory. Pass
  `isImmutable: true` when files do not change while the server runs; it allows
  caching. Other providers: `WithEmbeddedResources`, `WithZipFile`.
- Defaults: `index.html` as the default document, no directory listing (set
  `DirectoryLister = DirectoryLister.Html` to list), conditional requests and byte
  ranges supported.
- `%2F` and other encoded separators never act as path separators. A segment
  decoding to `/`, `\`, `.` or `..` is a 404. Containment in the served root is
  enforced.
- Precompressed `.br`/`.gz` siblings are served when
  `PrecompressedVariants` is set. See [precompressed files](docs/guides/precompressed-files.md).
- Several folders: register specific mounts first; for an overlay use
  `HandleMappingFailed(FileRequestHandler.PassThrough)`. See
  [multiple static folders](docs/guides/multiple-static-folders.md).

## 13. Security: what you must configure

Defaults protect against some abuse but not all of it. Read the
[request-limits guide](docs/guides/request-limits.md) for the current defaults.
For any server reachable from an untrusted network:

| Concern | Do this |
| --- | --- |
| Large bodies | `WithMaximumRequestBodyBytes(n)`, or per route with `WithRouteRequestLimits` |
| Slow or stalled clients | `WithRequestTimeout(t)` caps total request time. `WithRequestBodyReadTimeout(t)` caps each body read. No default bounds how long a body being read may take. |
| Too many requests in flight | `WithMaximumConcurrentRequests(n, queueLimit)` |
| Too many connections | `WithConnectionLimits(maximumConnections, ...)` (not with `EmbedIOHttp3`) |
| Header abuse | `WithRequestHeaderLimits(bytes, count)`. Pass both arguments: omitting the count removes the count limit. |
| Compressed uploads | Only enable `WithSupportCompressedRequests(true)` with a finite decompressed limit. |
| Request rate | `RateLimitingModule` with a `PartitionedRateLimiter<IHttpContext>`. See [rate limiting](docs/guides/rate-limiting.md). |
| Behind a proxy | `ForwardedHeadersModule` trusts nothing until you call `WithTrustedProxies(...)`. Read results with `context.GetForwardedRequest()`; the request itself is not rewritten. See [forwarded headers](docs/guides/forwarded-headers.md). |
| Authentication | `WithBasicAuthentication(baseRoute, m => m.WithAccount(user, password))` over HTTPS only. For tokens, check in `OnBeforeHandler` or an earlier module. |
| Banning abusers | `IPBanningModule` (instance APIs are scoped to that module) or `ClientBanningModule` (custom keys, CIDR allow/deny, optional persistent store). |
| Browser access | `WithCors(origins, headers, methods)` with explicit origins, not `*`, when credentials are involved. |
| Sessions | `WithLocalSessionManager(m => { m.CookieSecure = true; m.CookieSameSite = CookieSameSiteMode.Lax; })` on HTTPS. |
| Error detail | Keep the default empty 500 or problem details. Do not enable `ExceptionHandler.HtmlResponse` or `EmptyResponseWithHeaders` in production. |

## 14. Diagnostics and telemetry

- Logging goes to the `TraceSource` named `EmbedIO`
  (`EmbedIO.Diagnostics.Log.Source`). With the DI package,
  `loggerFactory.ForwardEmbedIODiagnostics()` forwards it to
  `Microsoft.Extensions.Logging`; dispose the returned registration after the
  server stops.
- `WithTelemetry()` (`net10.0` only) enables the `EmbedIO.Server` Meter and
  ActivitySource with standard `http.server.*` instruments.

Guide: [metrics and tracing](docs/guides/telemetry.md).

## 15. Dependency injection and hosting

- Package `EmbedIO-Neo.DependencyInjection`.
- `server.WithDependencyInjection(provider)` creates one scope per request;
  register it before modules that use services.
- `api.WithControllerServices<T>()` activates a controller with constructor
  injection. `[FromServices]` binds a parameter from the request scope.
- `services.AddEmbedIO(configureOptions, configureServer)` runs the server as a
  Generic Host `IHostedService`.

## 16. Trimming and Native AOT

- Supported on the `net10.0` asset only.
- Reflection-based controller registration (`WithController<T>()`) does not work.
  Use the source generator, which ships inside the package:

```text
[GeneratedControllerBinding(typeof(ItemsController), JsonSerializerContext = typeof(AppJson))]
internal static partial class ApiControllers { }

api.WithController(ApiControllers.ItemsController)
```

- Use `SendDataWithMetadataAsync`, `GetRequestDataWithMetadataAsync` and
  `Json.SerializeWithMetadata` with source-generated `JsonTypeInfo`.
- Generator diagnostics EMBEDIO001-012 explain unsupported controllers.

Guide: [trimming and Native AOT](docs/guides/trimming-and-native-aot.md).

## 17. Pitfalls checklist

- Registering a static folder at `/` before the API: the API never runs.
- Using `OnGet("/a", ...)` expecting an exact match: it also matches `/a/b`.
- Reading the request body twice, or with `[JsonData]` plus a manual read.
- Binding request JSON into a domain type (over-posting).
- Changing status or headers after writing the body.
- Putting unvalidated request text into a header, cookie or reason phrase.
- Using `ValueTask` as a controller return type.
- Configuring options or modules after the server started.
- Calling `RunAsync` twice, or reusing a stopped server.
- An `https` prefix with no certificate.
- Expecting HTTP/3 on `netstandard2.0` or without native QUIC.
- Relying on defaults for an internet-facing server instead of setting limits.
- Calling `WithRequestHeaderLimits(bytes)` without a count.
- Sending a WebSocket close code a server cannot use, such as 1006.
- Upgrading from v1 without reading the
  [v1-to-v2 migration guide](docs/compatibility/neo-v1-to-v2.md).

## 18. Where to read more

| Topic | Guide |
| --- | --- |
| First app | [Getting started](docs/guides/getting-started/README.md) |
| Files | [Static files](docs/guides/getting-started/files.md) |
| Limits and deadlines | [Request limits](docs/guides/request-limits.md), [body limits](docs/guides/request-body-limits.md) |
| HTTP/3 | [HTTP/3 listener](docs/guides/http3.md) |
| OpenAPI | [OpenAPI documents](docs/guides/openapi.md) |
| Graceful shutdown | [Draining with WebSockets](docs/guides/graceful-drain-websockets.md) |
| Trailers and 1xx | [Response field sections](docs/guides/response-field-sections.md) |
| Everything else | [Documentation index](docs/README.md) |
