# Opt-in request validation

This feature is unreleased. EmbedIO-Neo 2.3.0 does not include it.

Add `[Validate]` to a controller parameter to validate its bound value before the
action runs. Use standard `System.ComponentModel.DataAnnotations` attributes on
that parameter and its model properties. Without `[Validate]`, binding and action
behavior remain unchanged, even when the model has validation attributes.

## Complete example

```csharp
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using EmbedIO;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer("http://localhost:9696/")
    .WithProblemDetails(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null })
    .WithModule(new WebApiModule("/api", ResponseSerializer.Json(AppJson.Default.Options))
        .WithController(Controllers.OrdersController));
await server.RunAsync(stop.Token);

[GeneratedControllerBinding(typeof(OrdersController), JsonSerializerContext = typeof(AppJson))]
internal static partial class Controllers { }

[JsonSerializable(typeof(OrderInput))]
[JsonSerializable(typeof(string))]
internal sealed partial class AppJson : JsonSerializerContext { }

public sealed class OrderInput
{
    [Required, StringLength(80, MinimumLength = 2)]
    public string? Name { get; set; }

    [Range(1, 100)]
    public int Quantity { get; set; }
}

public sealed class OrdersController : WebApiController
{
    [Route(HttpVerbs.Post, "/orders")]
    public string Create([JsonData, Validate, Required] OrderInput input) => "accepted";
}
```

`POST /api/orders` with `{"Name":"","Quantity":0}` returns 400 with
`application/problem+json` and an `errors` object keyed by `input.Name` and
`input.Quantity`. Valid input reaches `Create`. `[Required]` on the parameter
rejects a JSON null; without it, null is valid unless another rule rejects it.
Malformed JSON and conversion errors remain binding errors, not model errors.

`WithProblemDetails` selects the existing error handler; setting its HTML fallback
to null makes errors JSON regardless of Accept. Validation throws a
`ProblemDetailsException`, so application error handlers can customize or replace
the response. The library does not replace existing handlers implicitly.

## Scope and execution

- Validation works with JSON, query, route and form values, after each opted-in
  parameter is bound. Authorization and before-handler hooks run first. The first
  invalid parameter stops binding and prevents the action from running; errors
  are collected within that parameter, not across every action parameter.
- The declared model type determines validation in both reflection and generated
  bindings. Public readable instance properties, inherited properties, arrays and
  typed `IEnumerable<T>` collections are traversed. Fields, indexers and properties
  with `[JsonIgnore]`/`Condition = Always` are excluded. Additional properties on a
  runtime subtype are not discovered. Dictionaries, untyped collections and
  declared `object` models are unsupported; use a concrete request DTO.
- Property errors use CLR names rooted at the action parameter, with indexed
  collection paths such as `input.Files[0].Name`. JSON naming policies do not
  rename error paths. Shared children are checked at every path; ancestor cycles
  stop recursion after validating the property's own rules.
- Custom `ValidationAttribute` rules and `IValidatableObject` are supported.
  Object-level results can name members. Validators run synchronously, must be
  safe for concurrent requests, and must not perform unbounded work. Exceptions
  from validators/getters remain server errors, not client validation errors.
  Validation contexts have an explicit display name and no service provider;
  display/resource metadata is not discovered automatically.
- Generated bindings construct rules and access properties directly. AOT callers
  must supply generated JSON metadata as usual. Reflection-dependent annotations
  (`Compare`, `CustomValidation`, type/string `Range` constructors and resource-type
  error messages) produce EMBEDIO015 for generated bindings; express those rules
  in an AOT-safe custom validator instead. Reflection registration can use them.

## Bounds and cancellation

`[Validate(MaxDepth = 16, MaxNodes = 1024, MaxErrors = 32)]` shows the defaults.
Depth counts every visited value, including the root, property values, collection
containers and elements. Nodes also bound enumerated object-validation results
and their member names. Traversal limits produce a 400 validation error; errors
stop at MaxErrors. Invalid settings fail binding registration/generation.
Allowed ranges are depth 1-64, nodes 1-100000 and errors 1-1024. Model metadata is
limited to 128 distinct declared types during binding.

Request cancellation is checked between values and rules. It cannot interrupt a
property getter, validator or enumerator blocked inside application code. These
limits bound validation work, not JSON parsing, body size or collection allocation;
configure [request limits](request-limits.md) separately. Do not apply validation
to a streaming reader or a dependency-injected service.

## OpenAPI

Generated parameter metadata adds validation constraints to the request schema
with `allOf`; response schemas and unannotated actions keep their original shape.
Required properties, non-null requirements, numeric ranges and string/collection
length bounds are described. Custom rules, whitespace checks and other business
rules still run only on the server.

DataAnnotations measures strings in UTF-16 code units, while JSON Schema counts
Unicode code points. Exported minLength is a conservative lower bound (half the
UTF-16 minimum, rounded up); maxLength is the UTF-16 maximum and may be looser for
supplementary characters. `x-embedio-minLengthUtf16` and
`x-embedio-maxLengthUtf16` retain StringLength's exact application bounds. Property
names honor JsonPropertyName and the serializer naming policy. Custom converters
or resolver modifiers that reshape/rename models need an application-authored
schema; the generator cannot infer arbitrary transformations. RegularExpression
is not translated into a different regex dialect. Recursive references and schema
traversal beyond 16 levels carry `x-embedio-validation-runtime-only`; validation
continues at runtime under the configured limits, while those deeper constraints
are not expanded into the document.

## Validation and performance

Tests cover reflection/generated binding parity, opt-in compatibility, rejected
inputs never reaching the action, controller cleanup, nested paths, cycles,
shared objects, cancellation, custom rules, limits, OpenAPI and diagnostics.
The Native AOT smoke exercises real HTTP and generated schemas. The
`request-validation` state-machine campaign checks overlapping requests against
independent validity expectations and preserves replay/shrink provenance:

```sh
dotnet run --project test/EmbedIO.Fuzz -c Release -- --state-machines 1729 30 request-validation
dotnet run --project test/EmbedIO.Fuzz -c Release -- --state-machines-replay test/EmbedIO.Fuzz/state-corpus/request-validation.json
dotnet run --project test/EmbedIO.Performance -c Release -- --request-validation
```

The microbenchmark reports plan construction plus first validation, warm
validation-only batch averages and allocation cost. Its first call includes JIT;
its percentiles are not endpoint latency percentiles. Existing unannotated
generated invokers emit no validation call, and reflection binding inserts no
validation wrapper for unannotated parameters. Only metadata is cached; values and validation results are fresh for each request.
No reflection scan occurs per validated request. .NET 10 uses framework DataAnnotations; the .NET Standard 2.0
asset adds the pinned System.ComponentModel.Annotations 5.0.0 dependency.
