# Typed request binding (2.15.0+)

Opt in with `[QueryObject]`, `[FormObject]`, `[HeaderField]`, `[CookieField]`
or `[RouteField]`. Existing query/form attributes, implicit route conversion,
JSON binding, targets and runtime dependencies retain their behavior.

## A complete application

This generated controller needs no JSON metadata for its request DTO. Its text
responses keep the example independent of JSON serialization configuration.

```csharp
using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer("http://127.0.0.1:9696/")
    .WithModule(new WebApiModule("/api", ResponseSerializer.None(false))
        .WithController(AppBindings.BooksController));
try { await server.RunAsync(stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

[GeneratedControllerBinding(typeof(BooksController))]
internal static partial class AppBindings { }
public sealed class BooksController : WebApiController
{
    [Route(HttpVerbs.Get, "/books")]
    public TextHttpResult Search([QueryObject(RejectUnknownFields = true), Validate] SearchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        HttpContext.CancellationToken.ThrowIfCancellationRequested();
        return HttpResults.Text(input.Page.ToString(CultureInfo.InvariantCulture) + "|" + input.Search + "|" + string.Join(",", input.Tags));
    }

    [Route(HttpVerbs.Post, "/search")]
    public TextHttpResult Form([FormObject, Validate] SearchInput input) => Search(input);

    [Route(HttpVerbs.Get, "/books/{id}")]
    public TextHttpResult ReadId([RouteField] BookId id) => HttpResults.Text(id.Value.ToString(CultureInfo.InvariantCulture));

    [Route(HttpVerbs.Get, "/preferences")]
    public TextHttpResult Preferences([HeaderField("X-Client-Revision")] int revision = 1,
        [CookieField("theme")] string theme = "light") =>
        HttpResults.Text(revision.ToString(CultureInfo.InvariantCulture) + "|" + theme);
}
public sealed class SearchInput
{
    [Range(1, 100)]
    public int Page { get; set; } = 1;
    [RequestField("q")]
    public string Search { get; set; } = "";
    public string[] Tags { get; set; } = Array.Empty<string>();
}
public readonly record struct BookId(int Value)
{
    public static bool TryParse(string? text, IFormatProvider? provider, out BookId result)
    {
        result = default;
        if (text is null || !text.StartsWith("book-", StringComparison.Ordinal)
            || !int.TryParse(text.AsSpan(5), NumberStyles.None, provider, out var value)) return false;
        result = new BookId(value);
        return true;
    }
}
```

`GET /api/books?page=2&q=hello&tags=red&tags=blue` returns
`2|hello|red,blue`. POST the same URL-encoded fields to `/api/search` with
`Content-Type: application/x-www-form-urlencoded`. `/api/books/book-7` returns
`7`. Query/form field names use ordinal case-insensitive comparison; distinct Unicode
names are not folded by culture. Cookie names match exactly.
Configure the existing [Problem Details handler](problem-details.md) if clients
need structured error bodies. Binding throws a 400 Problem Details exception;
the host's configured exception handler determines its wire representation.

## Models, defaults and duplicate values

DTOs must be concrete classes with a public parameterless constructor and one to
128 public readable/writable properties. Binding is flat: nested DTOs, collections
other than one-dimensional arrays, indexers, init-only and required properties are
unsupported. Inherited/overridden writable properties work; hidden writable
properties are rejected. Read-only properties are ignored. Use a dedicated input
DTO rather than an entity with privileged writable properties.

Missing properties keep their initializers. Missing header/cookie parameters use
the method's declared default, or the type's default when none is declared.
Presence is separate from validation: `[RequestField(Required = true)]`,
`[HeaderField(Required = true)]` and `[CookieField(Required = true)]` require a
value to exist; `[Validate]` enables existing DataAnnotations rules. An empty
string stays empty; empty nullable scalar values become null; empty non-nullable
numeric values fail. Arrays retain repeated values in order, including empty
strings or nullable entries. No comma splitting occurs.

Scalars reject duplicates by default, even identical duplicates. Set
`DuplicateValues = RequestDuplicateValues.First` or `.Last` explicitly when that
is the intended contract. For example:

```csharp
[RequestField("sort", DuplicateValues = RequestDuplicateValues.Last)]
public string Sort { get; set; } = "title";
```

Unknown DTO fields are ignored unless `RejectUnknownFields = true`. Query/form
bracket names are literal: `tags[]=red` does not bind `Tags`; use `tags=red`.
Aliased fields retain CLR property paths in conversion, required-field and
validation errors (for example `input.Page`). Source-wide decoding errors use an empty error key; field-budget errors
may identify the offending source name. Invalid values are not echoed in binding messages.

BCL string conversions use invariant culture and cached converters. Custom types
may expose a public static `bool TryParse(string, IFormatProvider, out T)` or
`bool TryParse(string, out T)` when no supported string converter exists. The
provider overload receives invariant culture and takes precedence. Nullable
reference annotations on string/provider parameters are supported. Return false
for invalid input; successful reference-type parsers must return a non-null value.
Format, overflow and argument exceptions become safe binding errors; other parser
or property-setter exceptions remain application failures. Parsers and setters
are application code, not a sandbox. Cached converters/parsers can run concurrently;
keep them thread-safe and do not retain request state.

`[RouteField]` opts a route placeholder into this parsing contract and requires
presence by default. An alias selects another placeholder; `Required = false`
allows a missing optional placeholder to use the declared parameter default.
Route arrays are unsupported. Unannotated route parameters keep their legacy
conversion behavior.

## Bounds and body ownership

The opted-in query/form path enforces these fixed limits before growing its
collections: 128 distinct fields, 1,024 total values, names of 1–128 UTF-16 code
units, 4,096 UTF-16 code units per value, and 65,536 UTF-8 bytes for decoded names
plus values. Each repeated pair charges its name again. Malformed Unicode,
percent escapes and percent-encoded UTF-8 fail with 400. Query source text is
limited to 262,144 characters; the listener's URL limit may reject it earlier.

Forms read at most 262,144 encoded bytes after request decompression and before
percent decoding, regardless of Content-Length. Content decoding is strict; the
media type must be `application/x-www-form-urlencoded` (otherwise 415). Listener,
request-body and decompression limits still apply. Larger or streaming uploads
should use [multipart](streaming-multipart.md) or [tus](resumable-uploads.md).

The binder passes the existing request cancellation token to body reads and
closes its request stream in all cases. Multiple FormObject parameters share one
bounded, read-only parsed source per request; DTO instances are always new. A
FormObject action cannot also declare legacy form, JSON or multipart body
binding. That conflict is rejected when registering reflection controllers or
compiling generated controllers. Custom hooks/handlers that consume the body
must coordinate ownership themselves.

Headers are matched case-insensitively and retain raw field values. Header/cookie
bound values use the decoded field budgets above; cookie source text also has a
65,536-character cap. Cookies use strict cookie octets, preserve literal percent
characters and never URL-decode. Invalid cookie syntax fails a cookie-bound
request. Listener header limits remain authoritative.

## Integration and security

Authentication and authorization still run before controller activation and
binding. Header/cookie values are untrusted input and never establish identity.
Use the authentication APIs for Authorization or authentication cookies. Input
binding does not add CSRF protection or authorization for a bound identifier.

Binding plans and parser delegates are prepared once; generated bindings call
typed constructors/setters/parsers directly and work under trimming/Native AOT.
Request DTOs need no JsonSerializerContext. Serialized response payloads still
need their normal JSON metadata. Reflection registration remains annotated as
requiring dynamic code. No performance improvement is claimed from these checks.

Existing caching/idempotency rules are unchanged. If a response varies by a
bound header or cookie, configure an appropriate cache policy/key/Vary or keep
it uncached; cookie and authorization eligibility checks still apply. Binding
an extra input does not automatically add it to an application fingerprint.

Generated OpenAPI flattens DTO fields, carries leaf validation metadata, declares
header/cookie parameters and binding 400/form 415 responses. Reserved headers
Accept, Content-Type and Authorization must be represented through media/security
configuration. Raw header/cookie arrays have no faithful standard comma-array
representation, so document building fails closed for those operations; exclude
with `[OpenApiIgnore]` and supply an explicit wire contract if needed. Custom
TryParse identifiers appear as strings; document further constraints explicitly.

## Verification and fuzzing

The `request-binding` state machine drives generated and reflection controllers
with six concurrent actors, independent payload/error-field oracles, malformed
query/form inputs, duplicate headers/cookies, custom IDs and recovery requests.
Replay and shrinking use the [existing runner](multi-connection-fuzzing.md):

```sh
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --state-machines 1729 1000 request-binding
dotnet run --project test/EmbedIO.Fuzz -c Release --no-build -- --request-binding-selftest
```

The self-test must detect both actor-body corruption and error-field corruption.
Regression tests additionally exercise HTTP/1, HTTP/2 and HTTP/3, authorization
ordering, source bounds, cleanup, generator diagnostics and OpenAPI. These bounded
checks provide evidence for the covered contracts, not exhaustive input coverage.