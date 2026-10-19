# Typed errors and validation (2.14.0+)

These helpers extend the [typed HTTP results](typed-results.md) surface in 2.14.0+.
Existing empty `BadRequest<T>()`, `NotFound<T>()`,
`Conflict<T>()`, dynamic `Problem(problem)` and automatic `[Validate]` behavior
remain unchanged. No runtime dependency or target is added.

## A complete application

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using EmbedIO;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer("http://127.0.0.1:9696/")
    .WithModule(new WebApiModule("/api", ResponseSerializer.Json(AppJson.Default.Options))
        .WithController(AppBindings.BooksController));
try { await server.RunAsync(stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

public sealed record Book(int Id, string Title);
[JsonSerializable(typeof(Book))]
internal partial class AppJson : JsonSerializerContext { }
[GeneratedControllerBinding(typeof(BooksController), JsonSerializerContext = typeof(AppJson))]
internal static partial class AppBindings { }
public sealed class BooksController : WebApiController
{
    private static readonly ConcurrentDictionary<int, Book> Books = new();
    private static readonly NamedRoutes Routes = NamedRoutes.FromController("/api", AppBindings.BooksController);

    [Route(HttpVerbs.Get, "/books/{id}")]
    [OpenApiOperation(OperationId = "get-book")]
    public HttpResult<OkResult<Book>, NotFoundProblemResult> Get(int id)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return Books.TryGetValue(id, out var book) ? HttpResults.Ok(book)
            : HttpResults.NotFound(new ProblemDetails(404) { Detail = "The requested book was not found." });
    }

    [Route(HttpVerbs.Post, "/books")]
    public HttpResult<CreatedResult<Book>, ValidationProblemResult, ConflictProblemResult> Create([JsonData] Book? book)
    {
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        if (book is null) return HttpResults.ValidationProblem(new Dictionary<string, string[]> { ["book"] = new[] { "A book is required." } });
        var errors = new Dictionary<string, string[]>();
        if (book.Id <= 0) errors["book.Id"] = new[] { "A positive identifier is required." };
        if (string.IsNullOrWhiteSpace(book.Title)) errors["book.Title"] = new[] { "A title is required." };
        if (errors.Count != 0) return HttpResults.ValidationProblem(errors);
        if (!Books.TryAdd(book.Id, book))
            return HttpResults.Conflict(new ProblemDetails(409) { Detail = "The book identifier already exists." });
        return HttpResults.CreatedAt(Routes, "get-book", new Dictionary<string, object?> { ["id"] = book.Id }, book);
    }
}
```

The dictionary is demonstration storage. Configure persistence, authentication and
access policies separately. Error helpers report an outcome; they do not perform
authorization. Supply source-generated payload metadata as above for Native AOT.

## Fixed errors and problem snapshots

`BadRequest(problem)`, `NotFound(problem)` and `Conflict(problem)` return concrete
400, 404 and 409 Problem Details results. A null problem or a mismatched problem
status fails at construction. For another status, use the existing dynamic
`HttpResults.Problem(new ProblemDetails(status))` helper.

The problem and extension nodes are copied on construction. `result.Problem` and
`result.GetProblem()` return independent copies. Each execution uses the existing
RFC 9457 writer and adds its requestId unless the application supplied one. The
module payload serializer is not used for error bodies. These are explicit
responses: exception-handler HTML negotiation and Customize callbacks do not run,
matching the existing `HttpResults.Problem` contract. Configure problem fields
before constructing the result and include only client-safe details.

## Validation dictionaries and DataAnnotations

`ValidationProblem(errors, detail)` sends a 400 problem with the title
`Request validation failed.` and an `errors` object mapping each field path to
its message array. An empty path represents a model-level error. Names are
ordinal and case-sensitive. All input messages are copied; later dictionary,
array, ValidationResult or member-name changes cannot change a response.
`ValidationProblemResult.Errors` is an immutable snapshot.

The second overload accepts `IEnumerable<ValidationResult?>` from
`System.ComponentModel.DataAnnotations`. It ignores `ValidationResult.Success`,
associates each message with every member name, and uses the empty path when
there are none. A null ErrorMessage becomes `The value is invalid.`. Null,
empty or whitespace-only messages in a dictionary are rejected. Neither overload
accepts an empty error set; only construct a validation response after finding an
error. These helpers do not run validators or enable `[Validate]`. Existing
[automatic validation](request-validation.md) still follows its configured
exception handler and traversal settings.

Both overloads reject input exceeding these fixed limits:

| Limit | Maximum |
| --- | ---: |
| Fields, total messages, or enumerated DataAnnotations results | 1,024 each |
| Field path | 1,024 UTF-16 characters |
| Individual message | 4,096 UTF-16 characters |
| Combined field/message text before JSON escaping | 64 KiB UTF-8 |

Null arrays, empty arrays, invalid UTF-16 and excessive input throw an argument
exception. Input is rejected rather than silently truncated. The text budget does
not include the optional application-provided detail. A caller-supplied enumerable
must still return from MoveNext; entry limits are not a producer timeout.

## OpenAPI, transport and replay

Generated bindings infer fixed error statuses and application/problem+json schemas
without registering the result classes or ProblemDetails in a JSON context.
Validation schemas describe the string-array field map. If a status can return
both a general problem and a validation problem, the schema permits the general
problem and its extension members; it does not require errors for every response.
Explicit OpenApiResponse attributes override inference. Inferred schemas describe
action results; binding, middleware and exception-handler responses still depend
on their configuration. Describe additional formats explicitly. Broad ErrorHttpResult or
HttpResult declarations retain a default response; concrete alternatives provide
more precise contracts.

Both generated and reflection controllers support synchronous, Task and ValueTask
errors. Existing controller lifetime, cancellation, HEAD/no-body handling and
security-header policies remain authoritative. Server output caching continues
to admit eligible 200 responses only; configure browser/proxy Cache-Control
separately. Eligible finite idempotency responses retain the stored body and its
original requestId on replay, as before. Shared result execution otherwise gets a
fresh context requestId each time.

## Testing and fuzzing

The optional testing package provides framework-independent inspection:

```csharp
using System.Collections.Generic;
using EmbedIO;
using EmbedIO.Testing;
var result = HttpResults.ValidationProblem(new Dictionary<string, string[]> { ["book.Name"] = new[] { "Required." } });
var problem = result.GetProblem();
var errors = result.GetValidationErrors();
```

GetProblem supports fixed and existing dynamic problem results. GetValidationErrors
requires a selected ValidationProblemResult and returns its immutable fields. Both
unwrap typed alternatives and reject other result kinds. Use a real request to
assert generated requestId, final security headers or transport behavior.

The typed-results shared-state model now checks three overlapping clients against
independent status, problem and validation-field expectations, with copied inputs,
Unicode/escaped messages, invalid input limits and cache isolation. Its negative
self-tests inject an actor mismatch and a wrong field. Focused regressions include
snapshot mutation, exact input boundaries, shared-status schemas, explicit
OpenAPI overrides, real HTTP/1–3, HEAD, idempotency and noncached errors.
