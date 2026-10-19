# Typed minimal endpoints and route groups

Version 2.16.0 adds opt-in exact endpoints without requiring a controller class.
Existing `OnGet` prefix callbacks and controller registration keep their behavior.
The new API uses the existing route matcher, bounded request binding, validation,
authorization, result execution and response policy infrastructure.

## A complete program

Install `EmbedIO-Neo` 2.16.0 or later in a .NET console application. This example
uses generated endpoint descriptors and JSON metadata, so its endpoint registration
also works with trimming and Native AOT.

```csharp
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Endpoints;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer("http://localhost:9696/").WithProblemDetails();
var endpoints = new EndpointModule(ResponseSerializer.Json(BooksJson.Default.Options));
server.WithModule(endpoints);
var books = endpoints.Root.MapGroup("/api/books");
books.MapGet("/", BookHandlers.ListEndpoint).WithName("list-books");
books.MapGet("/{id}", BookHandlers.GetEndpoint).WithName("get-book");
var document = new OpenApiDocumentBuilder("Books", "1.0")
    .AddEndpoints(endpoints).Build(BooksJson.Default.Options);
server.WithOpenApiDocument("/openapi.json", document);
Console.WriteLine(new Uri("http://localhost:9696/api/books?limit=1"));
try { await server.RunAsync(stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

[GeneratedEndpointBinding]
internal static partial class BookHandlers
{
    private static readonly Book[] Catalog = [new(1, "First book"), new(2, "Second book")];
    public static OkResult<Book[]> List([QueryObject, Validate] BookSearch input) =>
        HttpResults.Ok(Catalog.Take(input.Limit).ToArray());
    public static HttpResult<OkResult<Book>, NotFoundResult<Book>> Get(int id)
    {
        var book = Catalog.FirstOrDefault(item => item.Id == id);
        return book is null ? HttpResults.NotFound<Book>() : HttpResults.Ok(book);
    }
}
internal sealed class BookSearch
{
    [Range(1, 100), RequestField("limit")]
    public int Limit { get; set; } = 10;
}
internal sealed record Book(int Id, string Title);
[JsonSerializable(typeof(Book))]
[JsonSerializable(typeof(Book[]))]
internal partial class BooksJson : JsonSerializerContext { }
```

`ListEndpoint` and `GetEndpoint` are generated properties. The generator binds
parameters and calls the static method directly; registration does not discover
methods or compile expressions at runtime. Every public ordinary method on the
marked static partial class is a handler. Keep helpers private. Generic methods,
overloads with the same name, by-reference parameters/returns, async void,
unsupported binders and colliding generated property names fail at build time.
Containing types must also be partial and non-generic.

For an ordinary untrimmed application, method groups and lambdas also work:

```csharp
var books = server.MapGroup("/api/books");
books.MapGet("/{id}", BookHandlers.Get);
server.MapGet("/status", () => HttpResults.Ok("ready"));
```

These delegate overloads use reflection and expression compilation during
registration. They carry trimming/dynamic-code annotations. For AOT use the
explicit generated descriptor overload. `EndpointHandler.FromDelegate` prepares
a reusable delegate descriptor; its binding is compiled once per mapped route.
Captured and instance targets belong to the application, are shared across
requests, must support concurrency, and are never disposed by the endpoint API.
Register controller instances through the controller APIs instead.

## Routes, methods and names

- `MapGet`, `MapPost`, `MapPut`, `MapPatch`, `MapDelete`, `MapHead` and `MapOptions`
  map exact paths. `MapMethods(path, handler, HttpVerbs.Get, HttpVerbs.Post)` maps
  several explicit methods with one set of fluent settings.
- Nested `MapGroup` calls join application-relative prefixes. A prefix can contain
  placeholders; they participate in the handler's binding. `/` denotes the group's
  own path. Routes are case-sensitive by default and use existing EmbedIO route syntax.
  Set `module.CaseInsensitiveRoutes = true` before compilation for invariant
  case-insensitive literal matching; captured values retain their spelling.
- Required implicit route parameters must appear in the complete template.
  Missing optional placeholders with required parameters produce 400 responses;
  declared parameter defaults remain available. `[RouteField]` enables the bounded
  invariant custom-ID parser contract from typed request binding.
- Literal endpoints precede parameter routes. Other matches with equal parameter
  counts retain registration order. Avoid intentionally ambiguous parameterized
  templates. Equivalent templates for overlapping methods, conflicting `Any`
  registrations and duplicate names fail compilation.
- GET does not implicitly register HEAD. Register it explicitly when needed.
  A known path with another method returns 405 and `Allow`. Unknown paths pass to
  subsequent modules; an empty group does not claim requests.
- `.WithName("get-book")` supplies an application-relative link name and operation
  ID. `module.Links.GetPath("get-book", values)` uses the existing safe
  [named-route encoding rules](typed-results.md). Multiple-method operation IDs
  append the uppercase verb; they share one named path.

## Binding, results and services

The handler can return ordinary serialized values, `void`, `Task`, `Task<T>`,
`ValueTask`, `ValueTask<T>`, explicit `HttpResults`, typed alternatives and the
existing explicit streaming results. Async void is rejected.

Use `[QueryObject]`, `[FormObject]`, `[HeaderField]`, `[CookieField]`, `[RouteField]`,
`[JsonData]`, `[MultipartForm]` and `[Validate]` as in controllers. The existing
query/form field and request-data attributes remain available. The new bounded
DTO/field path keeps its duplicate, strict decoding, media-type and byte limits;
see [typed request binding](typed-request-binding.md). Known competing JSON,
multipart and form DTO body consumers are rejected. Custom request-data attributes
remain application code and must coordinate any additional body reads.

Unattributed `IHttpContext`, `IHttpRequest`, `IHttpResponse`, `RouteMatch`,
`IPrincipal` and `CancellationToken` parameters come from the current request.
The bare token is `context.CancellationToken`; it does not silently opt an
application into disconnect detection. `[RequestAborted] CancellationToken` uses
the existing explicit [request-abort policy](request-abort.md).

With the optional `EmbedIO-Neo.DependencyInjection` package, add
`server.WithDependencyInjection(provider)` before the endpoint module and use
`[FromServices]` parameters. Their request scope survives awaited result execution
and serialization; the request scope owns its services. Services and framework
parameters do not become OpenAPI request fields. A generated endpoint can use
`[FromServices]` without runtime controller discovery.

JSON request bodies need `JsonSerializerContext = typeof(MyJsonContext)` on
`GeneratedEndpointBinding` for AOT. JSON response metadata is configured separately
through the module serializer or explicit metadata-bearing result factories. A
query/form DTO does not need JSON serialization metadata merely to bind fields.

## Group policies and hooks

Configuration is declarative until `EndpointModule.Compile()`, module startup,
`AddEndpoints(module)` or first `module.Links` access. Each of those validates and
freezes the entire module. Configure all groups, endpoints and policy registries
first. Settings added to a group after mapping its children still apply. Normal
configuration is single-threaded application startup work.

`RequireAuthorization(policy, roles)` appends requirements; parent, handler and
endpoint requirements combine with AND. Roles in one requirement are alternatives.
The module's `ConfigureAuthorization` supplies named policy implementations.
`AllowAnonymous()` explicitly overrides inherited requirements; a subsequent
`RequireAuthorization()` restores enforcement of accumulated requirements. Unknown
policy names fail configuration even for anonymous endpoints. Existing
`[Authorize]`/`[AllowAnonymous]` handler and declaring-class attributes are honored.

The closest explicit output-cache, idempotency, browser-token and security-header
selection wins: group defaults, handler attributes, then endpoint fluent settings.
Use `CacheOutput`, `DisableOutputCache`, `WithIdempotency`, `DisableIdempotency`,
`WithSecurityHeaders` and `WithBrowserProtection`. Policy registries are configured
on `group.Module` using the existing module configuration methods. These remain
opt-in. Existing restrictions apply, including finite writes for idempotency,
no simultaneous output caching/idempotency, and no nonce-bearing response recording.
Host/origin protection remains active when only token checking is explicitly skipped.
Cache variation, authenticated identities and idempotency partitions remain
application policy; see the [cache](output-caching.md),
[idempotency](idempotency.md), [browser protection](browser-request-protection.md)
and [security-header](security-headers.md) guides.

`OnBeforeHandler` callbacks run outer group first, after authorization and browser
checks and before binding. A callback can write a response and call
`context.SetHandled()` to skip further before hooks and binding; completion hooks
still run. `OnAfterHandler` callbacks run inner scope first after
awaited invocation/serialization, including failures. All registered completion
callbacks are attempted; a completion callback does not replace an existing
invocation failure. Hooks use the request context and must finish asynchronously;
applications own their concurrency and cancellation. A committed response cannot
be replaced merely because a later callback fails. Idempotency replay skips
invocation hooks; output-cache requests still pass through these hooks. Controller
`BeforeHandler` attributes are not minimal endpoint hooks; use the fluent callbacks.

Add authentication, CORS, rate limiting and DI middleware **before the first
`server.Map*`/`GetEndpointModule` call**, which inserts the endpoint module at that
position. With an explicitly constructed module, add it after those modules.
Group authorization does not install an authentication provider. Server request
limits and per-route deadline/admission policies continue to apply normally.

## OpenAPI, tests and latency

`OpenApiDocumentBuilder.AddEndpoints(module)` reads the compiled route and binding
metadata, including flattened DTO fields, typed results and effective authorization.
`.WithName` supplies stable operation IDs; `.ExcludeFromDescription()` excludes a
specific registration. `WithSummary`, `WithDescription`, `WithTags` and `Produces<T>`
provide fluent metadata; `Produces` accepts integer and HttpStatusCode statuses and
only declares a response contract, without changing runtime result status. Group
tag arrays and compiled response declarations are copied. Handler-level OpenAPI
attributes are supported. Existing
fail-closed OpenAPI restrictions remain, including undeclared dynamic result
statuses, unsupported custom binders and credential/media headers. Supply actual
response declarations or explicitly exclude an operation rather than inventing a
contract. Delegate metadata discovery requires reflection; generated metadata does
not. Document schema generation still needs the appropriate JSON type metadata.

The `minimal-endpoints` campaign reuses the controller binding oracle with three
concurrent clients against generated and reflection minimal endpoints. It covers
strict decoding, duplicates, DTO validation, headers/cookies/custom IDs, nested
prefixes, named links, frozen configuration, actor isolation and recovery. The
`--minimal-endpoints-selftest` mode injects actor-body and error-field faults to
check the oracle. CI runs both paths on Linux and Windows. Native AOT smoke covers
generated binding, validation, authorization, framework/ValueTask injection, links
and OpenAPI.

The existing fresh-process latency harness adds `minimal-generated`,
`minimal-reflection`, `minimal-parameter` and `controller-parameter` scenarios.
It reports registration/listen time, first server request, first endpoint request
after a control endpoint, and warm p50/p95/p99 separately. Measurements include a
separate loopback HTTP client and are not promises about application/database work.
Binding plans and policy wrappers are prepared at compilation/startup; applications
using existing controller and callback APIs do not enable this module implicitly.
