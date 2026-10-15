# OpenAPI documents from controller bindings

EmbedIO-Neo writes an [OpenAPI 3.1](https://spec.openapis.org/oas/v3.1.0) document from the
Web API controllers registered through generated bindings (see
[Trimming and Native AOT](trimming-and-native-aot.md#web-api-controllers)). The document
comes from what the binding actually does, so its paths, parameters, request bodies and
response types follow the routes and binding rules that answer requests. Authentication
modules add their security requirements, and routes outside generated bindings are added
by declaration. It uses no reflection and works in trimmed and Native AOT applications.

This is new in Neo v2. The published 1.0.3 package does not
contain it; [OpenAPI and Swagger UI](../user-reports/openapi-and-swagger-ui.md) shows how to
serve a document you write yourself with any version.

## A complete program

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.WebApi;

var options = BooksJson.Default.Options;

// Replace the demo account with your own credential check.
var authentication = new BasicAuthenticationModule("/api", "Books").WithAccount("reader", "change-me");

var document = new OpenApiDocumentBuilder("Books", "1.0")
    .AddController("/api", ApiControllers.BooksController)
    .AddBasicAuthentication("basic", authentication)
    .AddOperation(HttpVerbs.Get, "/health", o => o.Response(200, "The service is up.", typeof(string), "text/plain"))
    .Build(options);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

using var server = new WebServer("http://localhost:9696/")
    .WithOpenApiDocument("/openapi.json", document)
    .WithModule(authentication)
    .WithModule(new WebApiModule("/api", ResponseSerializer.Json(options))
        .WithController(ApiControllers.BooksController))
    .WithRouting("/", routing => routing.OnGet("/health", false,
        (context, _) => context.SendStringAsync("up", "text/plain", WebServer.DefaultEncoding)));

Console.WriteLine(new Uri("http://localhost:9696/openapi.json"));
await server.RunAsync(cancellation.Token);

[GeneratedControllerBinding(typeof(BooksController), JsonSerializerContext = typeof(BooksJson))]
internal static partial class ApiControllers
{
}

[JsonSerializable(typeof(Book))]
[JsonSerializable(typeof(List<Book>))]
internal sealed partial class BooksJson : JsonSerializerContext
{
}

public sealed class Book
{
    public string Title { get; set; } = string.Empty;

    public int Year { get; set; }
}

internal sealed class BooksController : WebApiController
{
    private static readonly List<Book> Books = new() { new Book { Title = "Dune", Year = 1965 } };

    [Route(HttpVerbs.Get, "/books")]
    [OpenApiOperation(Summary = "List books", Tags = new[] { "Books" })]
    public List<Book> List([QueryField] int? since)
        => Books.FindAll(b => since == null || b.Year >= since);

    [Route(HttpVerbs.Get, "/books/{index}")]
    [OpenApiResponse(404, Description = "No book has that index.")]
    public Book Get(int index)
        => index >= 0 && index < Books.Count ? Books[index] : throw HttpException.NotFound();

    [Route(HttpVerbs.Post, "/books")]
    [OpenApiResponse(201, Type = typeof(Book), Description = "Added.")]
    public Task<Book> Add([JsonData] Book book)
    {
        Books.Add(book);
        Response.StatusCode = 201;
        return Task.FromResult(book);
    }

    [Route(HttpVerbs.Get, "/admin/reindex")]
    [OpenApiIgnore]
    public string Reindex() => "done";
}
```

`GET /openapi.json` returns the document as `application/json; charset=utf-8`; HEAD
returns its headers. Other methods and paths pass to the next module. The document is
served before the authentication module, so it needs no credentials; `/api` requests do,
and `/health` does not.

## What the document contains

Each route of a generated binding becomes one operation:

| Binding | Document |
| --- | --- |
| Route parameter `{index}` | A required path parameter, typed from the method parameter. An optional `{index?}` stays required, as OpenAPI requires, and is described as "May be empty." |
| `[QueryField]` | A query parameter. Arrays are arrays; enums list their names, as the binder accepts them. |
| `[QueryData]` | One query parameter holding every field, with form style and explode. |
| `[FormField]`, `[FormData]` | An `application/x-www-form-urlencoded` request body. |
| `[JsonData]` | An `application/json` request body referencing the type's schema. |
| Return value | A 200 response with the type's schema; `void` and `Task` give a 200 response without content. |
| Parameters that can fail to bind | A 400 response, which EmbedIO sends when a value cannot be converted. |

Types are described by the JSON schema exporter in System.Text.Json, from the
`JsonSerializerOptions` passed to `Build`, which should be the options the module
serializes with. Each type becomes a named entry under `components/schemas`, referenced
from the operations; recursive types reference themselves. With EmbedIO's default options
(`Json.CreateOptions()`), booleans, enums and dates go through EmbedIO's compatibility
converters, which the exporter cannot see into; the document states what they write:
`boolean`, `integer` and a `date-time` string. Those options also read numbers written as
strings, and the exporter describes that.

The operation ID defaults to the controller name without its `Controller` suffix and the
method name, such as `Books_List`, and the tag to the controller name without its suffix.

## Attributes

- `[OpenApiOperation]` on a method sets `Summary`, `Description`, `OperationId`, `Tags`
  and `Deprecated`.
- `[OpenApiResponse(status)]` on a method adds a response with `Description`, a `Type` for
  its JSON content and an optional `ContentType`. Repeat it for several responses. A
  declared 2xx response replaces the 200 described from the return type.
- `[OpenApiIgnore]` on a method or controller leaves it out of the document. The route
  still answers requests.

## Authentication

EmbedIO authenticates with modules: a module guards every request under its base route,
before the modules that serve them. The document follows the same rule.

- `AddBasicAuthentication(name, module)` describes a `BasicAuthenticationModule`: it adds an
  HTTP Basic security scheme with the module's realm, requires it on every operation whose
  path the module handles, and adds the 401 response the module sends, with its
  `WWW-Authenticate` header. A 401 the operation already declares is kept.
- `AddSecurityScheme(name, scheme)` adds any other scheme: `OpenApiSecurityScheme.Basic()`,
  `Bearer(bearerFormat)` or `ApiKey(name, location)` for a header, query field or cookie.
- `RequireSecurity(module, name)` requires a scheme on every operation the module handles,
  for a custom authentication module. `RequireSecurity(baseRoute, name)` does the same for a
  base route, when the check is not a module of its own, such as a controller's
  `OnBeforeHandler`.

Which operations a requirement covers is decided by the module's own `MatchUrlPath`, or by
EmbedIO's base-route matching for `RequireSecurity(baseRoute, name)`, so the document and the
server agree on the boundary: a module at `/api` covers `/api/books` but not `/apiary`.
Operations covered by several requirements need all of them, since all the modules run.

The document cannot see module order. A module registered after the one that serves a route
never sees its requests, and the document would still list the requirement. Register
authentication modules first, as in the program above.

## Routes outside generated bindings

Routes served by a `RoutingModule`, an `ActionModule` or a custom module have no binding to
read, so they are declared:

```csharp
builder.AddOperation(HttpVerbs.Get, "/files/{name}/{part?}", o =>
{
    o.Summary = "Read part of a file";
    o.Tags = new[] { "Files" };
    o.Parameter("part", OpenApiParameterLocation.Path, typeof(int))
        .Parameter("X-Trace", OpenApiParameterLocation.Header, typeof(Guid), required: true)
        .Response(200, type: typeof(string), contentType: "text/plain")
        .Response(404);
});
```

- The route uses EmbedIO route syntax and is the full path, including the module's base route.
- Route parameters are strings unless declared with another type, and always required; an
  optional `{part?}` is described as "May be empty."
- Query, header and cookie parameters take a type such as `int`, `Guid`, an enum (its names
  are listed) or an array of them.
- `RequestBody(type, contentType, required)` and `Response(status, description, type,
  contentType)` take their schemas from the serializer options for JSON media types. Other
  media types accept a `string`, a byte array or a `Stream`; binary content is described by
  its media type alone.
- A declaration must list its responses. Nothing checks it against the handler, so declare
  every status the handler sends.

## Routes the document cannot describe

Some routes have no faithful OpenAPI form. `Build` lists every such route and throws
`OpenApiDocumentException`, whose `Problems` property names each one, rather than writing
a document that describes a different API:

- a base route (`[BaseRoute]`), which matches any sub-path;
- `HttpVerbs.Any`, which answers every method;
- the QUERY method, which OpenAPI 3.1 has no field for;
- two operations with the same method and path;
- a parameter bound by a custom request-data attribute;
- a method reading the body both as JSON and as a form, or as JSON more than once;
- a response status outside 100 to 599, or declared twice;
- a type the serializer options have no metadata for;
- a binding generated before operation metadata existed;
- a declaration with no responses, an invalid route, a path parameter missing from its
  route, a parameter declared twice, an `Accept`, `Content-Type` or `Authorization` header
  parameter (OpenAPI ignores those; describe them with the body, the response or a security
  scheme), or a body type the media type cannot carry.

Mark a route `[OpenApiIgnore]` to leave it out. `Build(options, allowIncomplete: true)`
returns the document without those routes, with the same list in `Problems`.

## Limits

- Controllers registered through generated bindings are described from their bindings;
  other routes only as declared. A declaration is what you state, not what the handler does.
- Security requirements follow module base routes. Authorization inside a handler, such as
  a role check that answers 403, is not visible to the document; declare its responses on
  the operation.
- `AddController` takes the controller's base route; it is the path prefix the operations
  are written under. Pass the same route the `WebApiModule` uses.
- The document describes what the binding reads and what the method returns. A method that
  writes the response itself, sets a status the attributes do not declare or throws other
  HTTP exceptions answers with responses the document does not list unless declared with
  `[OpenApiResponse]`.
- The 400 response and `HttpException` responses carry EmbedIO's HTML error page unless
  the server's `HandleHttpException` callback writes something else, so the document
  gives them no content. Give an `[OpenApiResponse]` a `Type` only when your handler
  writes that JSON.
- OAuth 2.0 and OpenID Connect schemes have no factory. `Root` is a `JsonObject` an
  application can edit before serving the document, for example to add one.

## Swagger UI

The document works with any OpenAPI 3.1 tool. To serve Swagger UI from the same server,
follow [OpenAPI and Swagger UI](../user-reports/openapi-and-swagger-ui.md#optional-serve-swagger-ui-on-the-same-origin)
and point it at `/openapi.json`.

## Validation

Tests compare the document with the requests EmbedIO answers: every documented operation is
called over HTTP and each response status must appear in the document. CI also checks the
written documents with [openapi-spec-validator](https://github.com/python-openapi/openapi-spec-validator),
an implementation independent of EmbedIO, pinned in `.github/requirements/openapi.txt`.
