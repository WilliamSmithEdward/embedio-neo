# Routes, verbs, and parameters

A request has a verb (such as GET), a path (such as `/api/items/42`), and
optionally a query string or body. Use a controller to map these to C# methods.

| Task | Verb | Where input usually goes |
| --- | --- | --- |
| Read an item | GET | Route parameter: `/api/items/42` |
| Filter a list | GET | Query parameter: `/api/search?term=hello` |
| Create an item | POST | JSON body |
| Replace an item | PUT | Route parameter and JSON body |
| Remove an item | DELETE | Route parameter |

EmbedIO dispatches to your method; your method supplies the application behavior.
The example below echoes inputs so you can learn the request handling without
setting up a database. It does not save, replace, or delete stored items.

## Run the example

Use the project from [Your first JSON endpoint](README.md). Replace `Program.cs`
with this complete program:

```csharp
using System;
using System.Threading;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

using var server = new WebServer("http://localhost:9696/")
    .WithWebApi("/api", api => api.WithController<ItemsController>());

Console.WriteLine("Open http://localhost:9696/api/items/42");
Console.WriteLine("Press Ctrl+C to stop.");
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
    public object GetItem(string id) => new { id = ParseId(id) };

    [Route(HttpVerbs.Get, "/search")]
    public object Search([QueryField(true)] string term) => new { term };

    [Route(HttpVerbs.Post, "/items")]
    public object PostItem([JsonData] ItemInput input)
    {
        ValidateInput(input);
        return new { operation = "post", name = input.Name };
    }

    [Route(HttpVerbs.Put, "/items/{id}")]
    public object PutItem(string id, [JsonData] ItemInput input)
    {
        ValidateInput(input);
        return new { operation = "put", id = ParseId(id), name = input.Name };
    }

    [Route(HttpVerbs.Delete, "/items/{id}")]
    public object DeleteItem(string id) => new { operation = "delete", id = ParseId(id) };

    private static int ParseId(string id)
    {
        if (!int.TryParse(id, out var value))
            throw HttpException.BadRequest("Id must be a whole number.");
        return value;
    }

    private static void ValidateInput(ItemInput? input)
    {
        if (input == null || string.IsNullOrWhiteSpace(input.Name))
            throw HttpException.BadRequest("Name is required.");
    }
}

public sealed class ItemInput
{
    public string? Name { get; set; }
}
```

Run `dotnet run`. Leave it running while you try the requests below.

## Route and query parameters

Open these addresses in your browser:

| Address | JSON response |
| --- | --- |
| `http://localhost:9696/api/items/42` | `{"id":42}` |
| `http://localhost:9696/api/search?term=hello` | `{"term":"hello"}` |

`{id}` binds to the method parameter named `id`. Our `ParseId` helper converts
it to an integer and returns 400 Bad Request for an invalid number.
`[QueryField(true)]` reads the query field with the same name as the parameter
and requires it to be present.
The query is separate from the route: `/search?term=hello` matches `/search`.

Here `term` is required. For an optional query field, use
`[QueryField(false)] string term = ""` instead. Missing required query fields
produce 400 Bad Request.

## POST and PUT with JSON

In the project folder, create `item.json` containing:

```json
{"Name":"Notebook"}
```

Open a second terminal in that folder. These commands use `curl`; on Windows,
use `curl.exe` to avoid the older PowerShell alias.

```sh
curl -i -X POST http://localhost:9696/api/items -H "Content-Type: application/json" --data-binary "@item.json"
curl -i -X PUT http://localhost:9696/api/items/42 -H "Content-Type: application/json" --data-binary "@item.json"
```

POST returns `{"operation":"post","name":"Notebook"}`.
PUT returns `{"operation":"put","id":42,"name":"Notebook"}`.
Both return 200 in this demonstration.

`[JsonData]` reads the JSON body into `ItemInput`. Use public properties for the
request data. Invalid JSON produces 400 Bad Request; our `ValidateInput` method
also rejects a missing or blank name. Validation of application rules belongs
in your code.

The verb is part of the route. Opening `/api/items` in a browser sends GET,
so it will not call the POST method.

## DELETE

```sh
curl -i -X DELETE http://localhost:9696/api/items/42
```

The response is `{"operation":"delete","id":42}`. DELETE passes the same
route parameter as GET and PUT; this example does not need a body.

## Choose response status codes

Returning an object produces a JSON response with status 200 unless you set
another status. In a real create method, after successfully storing the item,
set `HttpContext.Response.StatusCode = 201` before returning its representation.
For a missing item, throw `HttpException.NotFound()`. For a response with no body,
set status 204 and use a method that returns `void` or `Task`.

## Limit compressed request bodies

The modern-engine development branch adds a decoded-byte limit to the existing
opt-in request decompression setting. Configure it while creating the server:

```csharp
using var server = new WebServer(options => options
    .WithUrlPrefix("http://localhost:9696/")
    .WithMode(HttpListenerMode.EmbedIO)
    .WithSupportCompressedRequests(true)
    .WithMaximumDecompressedRequestBodyBytes(1_048_576));
```

The request-stream helpers, including JSON/form/body readers, raise HTTP 413
when decoded content exceeds one MiB. The limit counts bytes, including all
bytes of UTF-8 characters; an exactly sized body is accepted after checking EOF.
Zero accepts only an empty decoded body. Null preserves the existing unlimited
behavior, and negative values are rejected during configuration. Request
compression remains disabled unless explicitly enabled.

The .NET 10 asset handles gzip, deflate and Brotli. The .NET Standard 2.0 asset
handles gzip/deflate and rejects Brotli explicitly. This setting applies when a
helper decodes a recognized compressed coding. Uncompressed/identity bodies and
direct reads of `Request.InputStream` need separate policies. For streaming
processing, read through EOF before treating the whole request as accepted;
data read before a later limit error may already have reached application code.
The limit does not bound native codec memory or replace transport/slow-peer
limits. These APIs are unreleased development work.

## Decode content-coding chains

The unreleased modern-engine branch accepts recognized Content-Encoding lists
when `SupportCompressedRequests` is enabled. For `Content-Encoding: gzip, br`,
the sender applies gzip first and Brotli second; the helpers undo Brotli and then
gzip. Up to eight compression layers are supported. Excess depth, unsupported
names and coding parameters are rejected with the existing HTTP 400 behavior.

The .NET 10 asset supports gzip, deflate and Brotli layers; the .NET Standard
asset supports gzip/deflate chains and explicitly rejects any Brotli layer.
Case-insensitive coding names and optional whitespace are accepted. Empty list
members are ignored; an empty field or identity-only list leaves the body raw.
These untransformed bodies retain the existing decoded-limit exclusion.

`MaximumDecompressedRequestBodyBytes` applies once to the final application
bytes, so compression-envelope overhead does not reject an empty body at limit
zero. It does not bound intermediate expansion CPU or native codec memory.
Streaming applications must read through EOF before treating the complete body
as accepted. The chain wrapper drives outer layers to EOF, preserves cancellation,
closes its owned source once and makes malformed-data failures sticky.

Deflate requests still select the legacy raw format. The helpers now validate
the complete raw stream and reject truncation or trailing bytes with HTTP 400.
An empty decoded body requires a valid empty DEFLATE stream, such as 03 00;
an absent compressed stream is malformed. Standards zlib selection, gzip
envelope validation and response coding-chain support remain development work.
Next: [Serve HTML and files](files.md) alongside this API, or
[await an outbound HTTP request](../async-outbound-requests.md).

## Advertise and validate QUERY formats

The unreleased engine branch adds `QueryFormatPolicy` for resources that support
QUERY. Create the policy once and apply it before writing response headers:

```csharp
var formats = new QueryFormatPolicy("text/plain;charset=utf-8");

server.WithAction("/search", HttpVerbs.Any, async context =>
{
    formats.Apply(context);
    context.Response.Headers[HttpHeaderNames.Allow] = "GET, HEAD, OPTIONS, QUERY";
    if (context.Request.HttpMethod == "QUERY")
    {
        var term = await context.GetRequestBodyAsStringAsync();
        var names = new[] { "alpha", "beta", "gamma" };
        var matches = names.Where(name => name.IndexOf(term,
            StringComparison.OrdinalIgnoreCase) >= 0);
        await context.SendStringAsync(string.Join("\n", matches), "text/plain",
            WebServer.Utf8NoBomEncoding);
    }
    else if (context.Request.HttpVerb is HttpVerbs.Get or HttpVerbs.Head or HttpVerbs.Options)
        await context.SendStringAsync("Send a UTF-8 text QUERY to search.",
            "text/plain", WebServer.Utf8NoBomEncoding);
    else
        throw new HttpException(405);
});
```

This fragment uses `System` and `System.Linq` and an existing configured `server`.
It advertises `Accept-Query: "text/plain";charset="utf-8"` on that resource's
responses, including discovery requests. The query component of the resource URI
does not change the policy. An unsupported QUERY Content-Type raises HTTP 415
with both `Accept-Query` and ordinary `Accept` format information. Missing,
wildcard or ambiguous Content-Type information is rejected with HTTP 400.

Media ranges support exact types, `type/*` and `*/*`. Configured parameters are
required constraints; extra request parameters are allowed. Names and media types
are case insensitive; charset values are case insensitive, while other configured
parameter values match exactly after quoted-string decoding. Discovery uses
Structured Fields strings, including numeric-looking parameter values. Parameter
names must fit Structured Fields keys and advertised values must be printable
ASCII; unrepresentable or duplicate configured parameters are rejected at setup.

The policy does not alter other methods' request processing, route requests,
read content, evaluate queries or implement conditional/range/cache semantics.
Handlers must validate query content and remain safe and idempotent. This example
performs a read-only text search; applying a policy does not make a mutating handler
safe. See [RFC 10008](https://www.rfc-editor.org/rfc/rfc10008.html#section-3).

## Evaluate selected-representation preconditions

The unreleased `Request.EvaluatePreconditions(entityTag, lastModified,
representationExists)` helper evaluates entity-tag and date conditions in HTTP
order. It returns null to continue, 304 for unchanged GET/HEAD/QUERY results, or
412 when a condition fails. Strong comparison is used for If-Match; weak comparison
is used for If-None-Match. Invalid dates are ignored, malformed entity-tag lists
raise HTTP 400, and server-supplied invalid tags raise an argument error.

Call it after authorization and ordinary validation, for a request that would
otherwise succeed. Supply validators for the selected representation. For QUERY,
that means the query results including request content, relevant metadata and
response content negotiation. A target-URI-only tag can incorrectly produce 304
for different queries. The helper neither reads the request nor derives validators.

Set the response's ETag/Last-Modified and other appropriate representation/cache
headers yourself. If the helper returns a status, set `Response.StatusCode` and
finish without writing content. Otherwise send the selected representation.
CONNECT, OPTIONS and TRACE conditions are ignored. The helper does not implement
range selection, caching, equivalent-resource URI assignment or automatic replay
of a previously successful state-changing operation. Existing conditional and
range helper behavior is preserved; adopting this evaluator is explicit.

See [RFC 9110 precondition ordering](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.2.2)
and [QUERY conditional requests](https://www.rfc-editor.org/rfc/rfc10008.html#section-2.6).
