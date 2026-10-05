# JSON responses with circular references

[Upstream #600](https://github.com/unosquare/embedio/issues/600) asks how to
configure `HttpContext.SendDataAsync(result)` when a response graph contains a
cycle. Neo 1.0.0 already accepts custom `System.Text.Json.JsonSerializerOptions`
through a response serializer. No additional JSON library is required.

## Opt in for a response

Create the callback once, then supply it explicitly when sending data:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbedIO;

var options = new JsonSerializerOptions
{
    ReferenceHandler = ReferenceHandler.IgnoreCycles,
    IncludeFields = true,
};
var serializer = ResponseSerializer.Json(options);

await HttpContext.SendDataAsync(serializer, result);
```

This uses fresh .NET response options, including the built-in handling of
`object`-typed members and dictionaries. It does not install a global setting.
Add any naming policy or converters your response contract requires explicitly.
`Json.CreateOptions()` also includes Neo's compatibility converters for untyped
deserialization; those are unnecessary for this response-only example. Custom
converters can change how reference tracking works, so validate the resulting
payload if you add them.

For buffered output, use `ResponseSerializer.Json(true, options)` instead.
The callback copies the options when created; changing the original options later
does not reconfigure it. Create a new callback to change its configuration.

For controllers that **return** objects, configure the module's serializer:

```csharp
using EmbedIO.WebApi;

var api = new WebApiModule("/api", serializer);
// Register the application's controller types on api, then add it to the server.
```

A controller that explicitly calls `HttpContext.SendDataAsync(result)` still uses
the default serializer. Pass `serializer` to that call as shown above; setting the
module's callback does not change the no-callback extension method.

## Payload semantics and compatibility

`ReferenceHandler.IgnoreCycles` writes a circular reference as JSON `null`. For
a typed node whose `Next` points back to itself, the payload is:

```json
{"Name":"root","Next":null}
```

This differs from Newtonsoft.Json's `ReferenceLoopHandling.Ignore`, which omits
looping properties. The ignored edge cannot be recovered by deserializing this
payload. Repeated references that are not cycles are serialized normally.
See [Microsoft's cycle-handling documentation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/preserve-references#ignore-circular-references)
and the [Newtonsoft example linked by the reporter](https://www.newtonsoft.com/json/help/html/ReferenceLoopHandlingIgnore.htm).

Neo's default behavior is unchanged: a cyclic graph without opt-in still throws
`JsonException`. If clients need a stable schema without null back-references,
map your objects to a response DTO that excludes those relationships instead.
`ReferenceHandler.Preserve` is a separate protocol choice that adds `$id`/`$ref`
metadata; do not enable it without considering your clients' JSON contract.

The upstream screenshot shows a debugger `$circref` marker, not a stack trace;
the original model and environment were not supplied. Regression tests cover
self-cycles, two-node cycles, object-typed dictionary cycles, repeated non-cyclic
references, both buffering modes, callback option snapshots, and unchanged default
rejection. Please share a minimal model if your payload behaves differently.
