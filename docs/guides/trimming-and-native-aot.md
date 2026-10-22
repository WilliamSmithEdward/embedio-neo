# Trimming and Native AOT

[Neo #138](https://github.com/WilliamSmithEdward/embedio-neo/issues/138) asks for
trimmed and Native AOT applications. The .NET 10 asset of EmbedIO is analyzed for both:
it builds with `IsAotCompatible`, so every API that needs reflection or runtime code
generation says so with `RequiresUnreferencedCode` and `RequiresDynamicCode`. A trimmed
or AOT application gets a warning where it calls such an API, rather than a failure at
run time. Web API controllers work through bindings that a source generator in the package
writes at build time. The .NET Standard 2.0 asset is not analyzed for trimming.

## What works

A Native AOT executable built from the
[smoke host](../../test/EmbedIO.AotSmoke/Program.cs) serves real HTTP with:

- routing modules and action modules with explicit routes and delegates;
- Web API controllers registered through generated bindings (below), with route, query,
  form and JSON parameters;
- JSON with source-generated metadata, through the `WithMetadata` methods;
- static files from `WithStaticFolder`;
- streamed responses;
- telemetry (`WithTelemetry`);
- HTTPS with HTTP/1.1, HTTP/2 and HTTP/3 from one `HttpListenerMode.EmbedIOCombined`
  listener, each checked by the negotiated version;
- WebSockets over TLS, with a `WebSocketMessageModule` echo;
- local sessions (`WithLocalSessionManager`), across requests;
- `EmbedIO.DependencyInjection`: request scopes, and a controller activated from request
  services through its generated binding, with a `[FromServices]` parameter;
- clean shutdown through the cancellation token.

Its publish runs with warnings as errors, so no trim or AOT warning is reachable from
these features. CI publishes and runs it on Windows (`windows-2025`, `win-x64`) and Linux
(`ubuntu-24.04`, `linux-x64`, with the pinned `libmsquic` 2.6.2 package for the smoke's
.NET HTTP/3 client).

## HTTP/3 in a Native AOT application

In unmerged PR #506, ordinary server HTTP/3 uses EmbedIO.Quic. The modern core
carries the owned native TLS runtime dependency; portable, RID-specific and
Native AOT package tests use normal NuGet selection, without a manual native
copy. Windows uses Schannel and needs no server `msquic.dll` copy. Linux/macOS
need their selected private TLS asset. Missing assets are rejected at public
server construction. Final supported-platform validation remains pending.

The following manual-copy workaround applies to the earlier provider in released
v2.16.0 and to any separate BCL HTTP/3 client in an application. Earlier server
HTTP/3 uses MsQuic. On Linux it is the system's `libmsquic`, installed as for any .NET
HTTP/3 application. On Windows, .NET ships `msquic.dll` in its runtime, but a Native AOT
publish leaves it out, so `QuicListener.IsSupported` is false and an HTTP/3 or combined
listener cannot start. Publish it beside the executable from the runtime pack:

```xml
<Target Name="PublishWindowsMsQuic" AfterTargets="ComputeLinkedFilesToPublish" Condition="$(RuntimeIdentifier.StartsWith('win-'))">
  <ItemGroup>
    <_MsQuic Include="@(RuntimePackAsset)" Condition="'%(Filename)%(Extension)' == 'msquic.dll'" />
    <ResolvedFileToPublish Include="@(_MsQuic)" RelativePath="msquic.dll" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Target>
```

Trimmed and Native AOT applications also turn off `HttpClient`'s own HTTP/3 support by
default (`Http3Support`). That affects only an application that is also an HTTP/3
client, as the smoke host is; set `<Http3Support>true</Http3Support>` there. The server
does not need it.

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

## Web API controllers

The EmbedIO-Neo package includes a source generator. For each controller named on a
`GeneratedControllerBinding` attribute, it writes a binding: the controller's routes and
the code that binds each request to the method's parameters, calls the method and
serializes the result. Registering the binding uses no reflection or runtime code
generation:

```csharp
[GeneratedControllerBinding(typeof(PeopleController), JsonSerializerContext = typeof(AppJson))]
internal static partial class ApiControllers
{
}

[JsonSerializable(typeof(Person))]
internal sealed partial class AppJson : JsonSerializerContext;

server.WithModule(new WebApiModule("/api", ResponseSerializer.Json(AppJson.Default.Options))
    .WithController(ApiControllers.PeopleController));
```

The generator adds a static property named after each controller to the partial class.
Register it with `WithController(binding)`, `WithController(binding, factory)`,
`RegisterController(binding)` or `RegisterControllerWithContext(binding, factory, release)`,
the binding counterparts of the reflection-based methods. The controller class itself
does not change.

A binding behaves like the reflection-based registration of the same controller. Tests
send the same requests to both and compare every response, covering:

- the same routes, including route attributes inherited from overridden methods;
- the same parameter rules: request-data attributes first, then a route parameter with
  the parameter's name, then the parameter's default value;
- the same conversions: the generator constructs the type converter that `TypeDescriptor`
  returns for the parameter type, including a type's own `TypeConverterAttribute`, and the
  same 400 responses for invalid values;
- `[QueryField]`, `[FormField]`, `[QueryData]`, `[FormData]` and custom request-data
  attributes;
- result serialization through the module's serializer, for `void`, `Task`, `Task<T>` and
  other return types;
- `OnBeforeHandler`, and disposal of a disposable controller when the method returns its
  task, before the task completes. `RegisterControllerWithContext` releases after
  completion, as with reflection.

`[JsonData]` parameters use the attribute's `JsonSerializerContext`. Without one they use
EmbedIO's default JSON options, which need reflection: in a trimmed or AOT application
they throw `NotSupportedException`, and the generator warns (EMBEDIO010) when the project
runs the trim or AOT analyzer. Return values go through the module's serializer, so an AOT
application constructs the module with `ResponseSerializer.Json(options)` from its context,
as above.

Where the reflection-based registration fails at startup or per request, the generator
reports an error at build time instead:

| Diagnostic | Meaning |
| --- | --- |
| EMBEDIO001 | The class with the attribute, or a class containing it, is not partial, or is generic. |
| EMBEDIO002 | The type is not a non-generic `WebApiController` subclass accessible from the class. |
| EMBEDIO003 | Two controllers would produce properties with the same name, or the class already has a member with that name. |
| EMBEDIO004 | `JsonSerializerContext` is not a `JsonSerializerContext` type. |
| EMBEDIO005 | The controller has no public instance methods with route attributes. |
| EMBEDIO006 | A route attribute is a custom subclass; only `RouteAttribute` and `BaseRouteAttribute` are supported. |
| EMBEDIO007 | A route is null. |
| EMBEDIO008 (warning) | A method has a route attribute but is not a public, non-generic instance method, so neither registration uses it. |
| EMBEDIO009 | A parameter cannot be bound: a `ref`, `out` or `in` parameter, a ref struct, no converter for a field or route value, an empty field name, or request-data attributes that are ambiguous or unsuitable. |
| EMBEDIO010 (warning) | `[JsonData]` without a `JsonSerializerContext`, in a trimmed or AOT project. |
| EMBEDIO011 | The method returns by reference, or a type the serializer cannot receive. |
| EMBEDIO012 (warning) | The method returns `ValueTask` or `ValueTask<T>`, which is serialized as an object rather than awaited, as with reflection. |

| EMBEDIO013 | Authorization attributes are invalid or contradictory. |
| EMBEDIO014 | A before-handler declaration names an invalid/ambiguous hook or behavior (2.3.0+). |

[Before-handler attributes](route-hooks.md) generate direct hook calls before
parameter binding; they do not use reflection at request time. The AOT smoke host
exercises an asynchronous class hook, an action Skip override, authorization and
enum response/OpenAPI statuses.

Differences from the reflection-based registration:

- Parameters with a `null` default, such as `string? name = null`, work. The reflection
  binder rejects them at registration.
- Registration parses every route before adding any handler, so an invalid route leaves
  the module unchanged.
- Converters are chosen at build time. A `TypeDescriptor` provider added at run time is not
  consulted.
- Generic controller types and custom route attribute subclasses are not supported
  (EMBEDIO002, EMBEDIO006).

The attributes themselves need no reflection. Their runtime-type conversion paths, which
only the reflection-based binder calls, sit behind the feature switch
`EmbedIO.WebApi.IsReflectionBindingSupported`; it is enabled unless an application sets
it.

The generator targets Roslyn 4.8, so it runs in the .NET 8 SDK and later. Its generated
code also compiles as C# 7.3 in .NET Standard 2.0 libraries.

## What needs reflection

These features are annotated and not supported in trimmed or AOT applications:

- **Reflection-based controller registration**: `WithController<T>()`,
  `RegisterController(Type)` and the other overloads without a binding. Use generated
  bindings, above.
- **`WithControllerServices<T>()` and `WithControllerServices(Type)` in
  `EmbedIO.DependencyInjection`**, which bind the controller through reflection. Use
  `WithControllerServices(binding)` with a generated binding: it activates the controller
  from request services the same way, and `[FromServices]` parameters work.
- **Attribute-scanned routing handlers**: `RoutingModule.AddFrom` and
  `WithHandlersFrom`. Register routes with `OnGet`, `OnPost` and the other explicit methods.
- **EmbedIO's default JSON options**, as above.

## Measured

On one Windows 11 workstation (.NET 10.0.12), five alternating runs of the smoke host's
checks, each process started cold, before the host gained its controller checks:

| Build | First response after process start | Working set at exit | Deployment |
| --- | --- | --- | --- |
| Native AOT (`win-x64`) | about 20 ms | about 21 MB | one 10.4 MB executable |
| JIT, framework-dependent | about 114 ms | about 51.5 MB | 2.4 MB plus the installed .NET runtime |

These figures come from a single machine and a single short workload; they are not
throughput results. CI records the native executable's size and working set on each
platform in the `aot-smoke-*` artifacts.

## Not yet validated

Native AOT is exercised on Windows and Linux x64 only; macOS, ARM64 and mobile Native
AOT have not been run. The smoke host checks one representative of each feature, not
every option: for example, WebSockets over HTTP/2, the CLI and
`EmbedIO.JsonServer` have not been exercised as Native AOT.

The generated binding's handling of the plain `Exception` that .NET Framework's numeric
converters throw for invalid route values mirrors the reflection binder, but tests run on
.NET 10, where those converters throw `ArgumentException`.
