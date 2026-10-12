# Trimming and Native AOT

[Neo #138](https://github.com/WilliamSmithEdward/embedio-neo/issues/138) asks for
trimmed and Native AOT applications. The .NET 10 asset of EmbedIO is analyzed for both:
it builds with `IsAotCompatible`, so every API that needs reflection or runtime code
generation says so with `RequiresUnreferencedCode` and `RequiresDynamicCode`. A trimmed
or AOT application gets a warning where it calls such an API, rather than a failure at
run time. The .NET Standard 2.0 asset is not analyzed for trimming.

## What works

A Native AOT executable built from the
[smoke host](../../test/EmbedIO.AotSmoke/Program.cs) serves real HTTP/1.1 with:

- routing modules and action modules with explicit routes and delegates;
- JSON with source-generated metadata, through the `WithMetadata` methods;
- static files from `WithStaticFolder`;
- streamed responses;
- telemetry (`WithTelemetry`);
- clean shutdown through the cancellation token.

Its publish runs with warnings as errors, so no trim or AOT warning is reachable from
these features. CI publishes and runs it on Windows (`windows-2025`, `win-x64`) and Linux
(`ubuntu-24.04`, `linux-x64`).

## JSON

Use source-generated metadata from a `JsonSerializerContext`:

```csharp
[JsonSerializable(typeof(Item))]
internal sealed partial class AppJson : JsonSerializerContext;

server.WithModule(new RoutingModule("/api")
    .OnGet("/items/{id}", (context, route) =>
        context.SendDataWithMetadataAsync(new Item { Id = int.Parse(route["id"]) }, AppJson.Default.Item))
    .OnPost("/items", async (context, _) =>
    {
        var item = await context.GetRequestDataWithMetadataAsync(AppJson.Default.Item);
        await context.SendDataWithMetadataAsync(item, AppJson.Default.Item);
    }));
```

| API | Trimmed and AOT applications |
| --- | --- |
| `SendDataWithMetadataAsync(data, JsonTypeInfo<T>)`, `GetRequestDataWithMetadataAsync(JsonTypeInfo<T>)` | supported; UTF-8 `application/json`, malformed input is HTTP 400 |
| `Json.SerializeWithMetadata(data, JsonTypeInfo<T>)`, `Json.DeserializeWithMetadata(json, JsonTypeInfo<T>)` | supported |
| `Json.Serialize(data, options)`, `ResponseSerializer.Json(options)`, `RequestDeserializer.Json<T>(options)` | supported when `options.TypeInfoResolver` is a `JsonSerializerContext`; types it does not declare throw `NotSupportedException` |
| `SendDataAsync(data)`, `GetRequestDataAsync<T>()`, `ResponseSerializer.Default`, `Json.CreateOptions` and the other default-option paths | reflection-based; annotated, and they throw `NotSupportedException` when reflection JSON is disabled |

The `WithMetadata` methods use the metadata's own options; EmbedIO's lenient default
input handling (quoted numbers, trailing commas and so on) applies only to its default
options.

### The reflection switch

EmbedIO's default JSON options resolve metadata by reflection. The package carries a
build file that ties the feature switch `EmbedIO.Serialization.IsReflectionEnabled` to the
application's `JsonSerializerIsReflectionEnabledByDefault`, which trimmed and Native AOT
publishing set to `false`. Trimming then removes EmbedIO's reflection JSON paths, and
calling them throws:

```
NotSupportedException: Reflection-based JSON serialization is disabled in this application,
for example by trimming or Native AOT. Use the overloads that take a JsonTypeInfo, or
JsonSerializerOptions whose TypeInfoResolver is a JsonSerializerContext.
```

A project with `<PublishAot>true</PublishAot>` behaves this way under `dotnet run` and in
tests too, because the SDK applies the same runtime switches to ordinary builds. An
application that sets `JsonSerializerIsReflectionEnabledByDefault` to `true` gets the
reflection paths back, with the trimming warnings that come with them.

## What needs reflection

These features are annotated and not supported in trimmed or AOT applications:

- **Web API controllers**: `WithWebApi(...).WithController<T>()`, `RegisterController`
  and the request-data attributes (`[JsonData]`, `[QueryField]`, `[FormField]`). Controllers
  are discovered, bound and invoked through reflection and compiled expressions. A source
  generator for controller binding is the next part of #138.
- **Attribute-scanned routing handlers**: `RoutingModule.AddFrom` and
  `WithHandlersFrom`. Register routes with `OnGet`, `OnPost` and the other explicit methods.
- **EmbedIO's default JSON options**, as above.

## Measured

On one Windows 11 workstation (.NET 10.0.12), five alternating runs of the smoke host's
checks, each process started cold:

| Build | First response after process start | Working set at exit | Deployment |
| --- | --- | --- | --- |
| Native AOT (`win-x64`) | about 20 ms | about 21 MB | one 10.4 MB executable |
| JIT, framework-dependent | about 114 ms | about 51.5 MB | 2.4 MB plus the installed .NET runtime |

These figures come from a single machine and a single short workload; they are not
throughput results. CI records the native executable's size and working set on each
platform in the `aot-smoke-*` artifacts.

## Not yet validated

The smoke host covers HTTP/1.1 over plain TCP. HTTPS, HTTP/2, HTTP/3, WebSockets,
sessions, the `EmbedIO.DependencyInjection` package and platforms other than Windows and
Linux x64 have not been exercised as Native AOT.
