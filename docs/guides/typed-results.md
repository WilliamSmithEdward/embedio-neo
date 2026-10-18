# Typed HTTP results (unreleased)

These APIs are on the feature branch and are **not in NuGet 2.13.0**. They require
no new core dependency. Existing object/string/Task controller returns keep their
serializer and disposal behavior. Only a declared `HttpResult` type (including
its subclasses and supported Task/ValueTask wrappers) opts into this execution path.

## A complete controller application

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var api = new WebApiModule("/api", ResponseSerializer.Json(AppJson.Default.Options))
    .WithController(AppBindings.BooksController);
using var server = new WebServer("http://127.0.0.1:9696/").WithModule(api);
try { await server.RunAsync(stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

public sealed record Book(int Id, string Title);
[JsonSerializable(typeof(Book))]
internal partial class AppJson : JsonSerializerContext { }
[GeneratedControllerBinding(typeof(BooksController))]
internal static partial class AppBindings { }
public sealed class BooksController : WebApiController
{
    private static readonly ConcurrentDictionary<int, Book> Books = new();
    private static readonly NamedRoutes Routes = NamedRoutes.FromController("/api", AppBindings.BooksController);

    [Route(HttpVerbs.Get, "/books/{id}")]
    [OpenApiOperation(OperationId = "get-book")]
    public HttpResult<OkResult<Book>, NotFoundResult<Book>> Get(int id)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return Books.TryGetValue(id, out var book)
            ? HttpResults.Ok(book)
            : HttpResults.NotFound<Book>();
    }

    [Route(HttpVerbs.Post, "/books")]
    public HttpResult<CreatedResult<Book>, BadRequestResult<Book>, ConflictResult<Book>> Create([JsonData] Book? book)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        if (book is null || book.Id <= 0 || string.IsNullOrWhiteSpace(book.Title)) return HttpResults.BadRequest<Book>();
        if (!Books.TryAdd(book.Id, book)) return HttpResults.Conflict<Book>();
        return HttpResults.CreatedAt(Routes, "get-book", new Dictionary<string, object?> { ["id"] = book.Id }, book);
    }
}
```

This sample's in-memory dictionary is demonstration storage. Choose application
persistence and configure authentication/authorization separately. Successful
payloads use the module's configured serializer, including source-generated JSON
options. `HttpResults.Json(value, AppJson.Default.Book)` instead selects explicit
JSON metadata for that response, independently of the module serializer.

`HttpResults.Ok<Book>(book)` and `HttpResults.Ok(book)` are equivalent. C# infers
the type from the value. `NotFound<Book>()` needs the explicit type because it
has no value from which to infer it. A method returning `HttpResult<Book>` can
return either, with `Value` and `HasValue` available for inspection.

## Execution and OpenAPI

Declare `HttpResult<TFirst,TSecond>` or `HttpResult<TFirst,TSecond,TThird>` with
distinct concrete alternatives for precise generated response metadata. Generated
bindings describe Ok/Json (200), Created (201), Accepted (202), NoContent (204),
BadRequest (400), NotFound (404) and Conflict (409), with body schemas only for
payload-bearing alternatives. Explicit `[OpenApiResponse]` attributes override
inferred entries of the same status.

Broad `HttpResult`, `HttpResult<Book>`, dynamic-status text/redirect/Problem results
and file responses cannot enumerate every status from their type alone. Their
document includes a conservative `default` response rather than inventing a 200
body or claiming an exhaustive list. Add response attributes to describe their
known statuses/media types. Dynamic alternatives retain this fallback even when
some concrete alternatives can be inferred.

Synchronous, `Task<TResult>` and `ValueTask<TResult>` results execute once awaited;
ValueTask is awaited once. Null explicit results fail as server errors. Controllers
using explicit results remain alive through asynchronous result execution and are
released once afterward; existing ordinary-controller disposal timing is unchanged.
Do not hide a result behind an `object` return type: ordinary object serialization
is intentionally preserved. Serialization continues to follow the configured
serializer, including any custom serializer's cancellation behavior.

Direct handlers can `await result.ExecuteAsync(context, serializer)`. Without a
serializer, payload results use the normal reflection-based JSON default. Native
AOT applications must supply source-generated module options or explicit JsonTypeInfo.
Status-only/text/file/Problem results do not need reflection JSON serialization.
Payload objects remain application-owned; avoid mutating them while serializing or
sharing mutable payloads between concurrent requests.

## Other response helpers

- `Created(location, value)` and `Accepted(value, location)` carry a payload and
  optional/required Location as documented by their signatures.
- `Text(text, contentType, status)` writes UTF-8 text. Status accepts int or
  HttpStatusCode. Body-bearing statuses exclude 204, 205 and 304.
- `NoContent()`, `NotFound()` and `Status(int or HttpStatusCode)` return empty
  final responses. Status accepts 200 through 599; use existing informational
  response APIs for 1xx.
- `Problem(new ProblemDetails(HttpStatusCode.Conflict) { Detail = "..." })` reuses
  RFC 9457 output, including requestId. The problem and extension nodes are copied
  at construction and per execution. It does not throw an exception or expose a
  stack trace. Only include client-safe details.
- `Redirect(location, status)` accepts 301, 302, 303, 307 or 308, with int and enum
  overloads. Explicit Locations must be root-relative or HTTP(S), contain escaped
  ASCII, omit credentials and controls, and cannot be protocol-relative. The
  application must still authorize any external destination derived from user input.

## Named URLs

`NamedRoutes` copies an ordinal name-to-template registry. `FromController` derives
complete paths from generated bindings and an explicit module base route; names
are OpenApiOperation.OperationId, or `ControllerName.MethodName` (including the
Controller suffix). It never registers routes itself. Duplicate inferred names,
including multiple aliases/verbs on one method, fail; provide an explicit registry
for those cases. Base routes require explicit complete templates.

`GetPath(name, values)` uses invariant formatting and URI-escapes each parameter.
It rejects unknown names, extra/missing values, path separators, percent signs,
controls and dot segments, including dot segments produced by optional omissions.
Optional whole segments can be omitted. Registries allow at most 1,024 names,
128 characters/name and 4,096 characters/template; values are bounded to 2,048
characters and final paths to 8,192. Names are case-sensitive. Templates use safe
ASCII literals and the existing route grammar. Keep explicit templates synchronized
with actual route registration.

Generated Locations are relative to the application's root. No incoming Host,
Forwarded or X-Forwarded header is trusted. Deployment under a proxy path prefix
requires an application-configured base route; absolute URLs require an explicit
trusted application base URI outside this helper.

## Files, streams and ownership

`Bytes(bytes, contentType, downloadName, entityTag, lastModified)` copies the byte
array. `File(path, ...)` captures a full application-selected path and opens it
read-only at execution. Never pass a raw client filename as a file path; resolve
and authorize it within application storage. Each execution has its own source
handle. Keep file bytes stable while sending.

Both reuse existing representation preconditions and bounded byte ranges, including
HEAD, 206, 304 and 412. HEAD ignores Range, matching the existing helper. Range
errors use existing configured HTTP exception handlers. Download names are encoded
as RFC 5987 filename* and reject controls or path separators. Existing HTTP response
framing/no-body rules and selected security policies remain authoritative.

`Stream(stream, contentType, downloadName, leaveOpen)` accepts one readable source.
Seekable streams represent the whole resource from position zero; nonseekable
streams send from their current position without ranges. Ownership transfers only
when construction succeeds, unless leaveOpen is true. Execute once. An owned source
closes on success, cancellation and failure; dispose the result when abandoning it
before execution. A second concurrent execution fails without closing the active
owner. Do not dispose a result or mutate/share its stream while execution is active.
Cleanup failures are retained alongside body failures; committed failures abort
rather than pretend the body completed successfully.

A typed alternative delegates cancellation and cleanup to its selected result. If
a stream alternative is abandoned before execution, dispose its `SelectedResult`
as `IDisposable` (or retain and dispose the original stream result).

Copy operations observe the context cancellation token and transport errors.
Request-abort remains opt-in: long-lived producers should also cooperate with the
existing `[RequestAborted]` token or `GetRequestAbortedToken()` and keep deadlines.
Returning a stream does not make an arbitrary blocking producer cancellable.

## Cache, idempotency and testing

Finite explicit results work through existing output-cache and idempotency wrappers.
Static files and streams bypass cache publication and reject idempotent recording,
including when selected through a broad result at runtime. Declared file/stream
controller types are treated as streaming during registration. Security guards run
before existing cache/replay admission. Configure cache keys and invalidation for
all representation dimensions; a cached hit intentionally skips action execution.

Selected security-header policies still prepare final headers. Nonce policies force
no-store and bypass caches, and continue to reject 206/304 and idempotent recording.
Result helpers cannot override those guarantees.

`SelectedResult`, `StatusCode`, `ContentType`, immutable `Headers`, `HasValue` and
`Value` support direct assertions. Optional `EmbedIO-Neo.Testing` adds
`result.GetPayload<Book>()`, which unwraps alternatives and rejects empty/wrong-type
results. Use TestWebServer for execution tests and real listeners for transport
framing. The typed-results state-machine campaign exercises three gated clients,
cache admission, actor/body isolation, empty/error statuses, named URLs, nonce
binding and rejected path boundaries; its self-test injects a wrong actor payload.
