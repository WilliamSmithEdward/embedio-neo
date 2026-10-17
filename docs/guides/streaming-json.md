# Streaming JSON responses

This feature is unreleased; EmbedIO-Neo 2.4.0 does not include it.

Return `JsonStream.Array(source, itemTypeInfo)` or
`JsonStream.Ndjson(source, itemTypeInfo)` to stream an `IAsyncEnumerable<T>`.
Both return explicit response types understood by reflection and generated
controller bindings. Existing return values and module serializers keep their
behavior. A stream result bypasses the module serializer; configure its item
`JsonTypeInfo<T>` explicitly. No runtime dependency or target is added.

## Complete server

```csharp
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

using var stop = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new WebServer("http://localhost:9696/")
    .WithWebApi("/api", module => module.WithController(StreamBindings.ItemsController));
try { await server.RunAsync(stop.Token); }
catch (System.OperationCanceledException) when (stop.IsCancellationRequested) { }

[GeneratedControllerBinding(typeof(ItemsController), JsonSerializerContext = typeof(StreamJson))]
internal static partial class StreamBindings { }

public sealed class ItemsController : WebApiController
{
    [Route(HttpVerbs.Get, "/items")]
    public JsonArrayStream<Item> Array() => JsonStream.Array(Items(), StreamJson.Default.Item);

    [Route(HttpVerbs.Get, "/items.ndjson")]
    public Task<NdjsonStream<Item>> Lines() => Task.FromResult(JsonStream.Ndjson(
        Items(), StreamJson.Default.Item,
        new JsonStreamOptions(maxItemBytes: 64 * 1024, flushEvery: 1)));

    private static async IAsyncEnumerable<Item> Items(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var id = 1; id <= 100; id++)
        {
            await Task.Delay(10, cancellationToken);
            yield return new Item { Id = id };
        }
    }
}

public sealed class Item { public int Id { get; set; } }
[JsonSerializable(typeof(Item))]
internal sealed partial class StreamJson : JsonSerializerContext { }
```

For reflection registration, replace the generated binding with
`module.WithController<ItemsController>()`. Declare the concrete
`JsonArrayStream<T>` or `NdjsonStream<T>` return type, or `Task` of that type,
so OpenAPI can describe its framing. `ValueTask` results retain existing
controller semantics and are not introduced by this feature. In an action or
custom module, call `await result.WriteAsync(context, cancellationToken)`.
Use generated bindings and generated item metadata for trimming/Native AOT.

## What the client receives

Array mode uses `application/json`, for example `[1,2,3]`. An ordinary JSON
client can still read the entire response, while an incremental reader can
process items before the array ends:

```csharp
using var response = await client.GetAsync(url,
    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
response.EnsureSuccessStatusCode();
await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<Item>(
    body, cancellationToken: cancellationToken))
{
    if (item is not null) Process(item);
}
```

This client fragment uses `System.Net.Http` and `System.Text.Json` on .NET 10;
use its metadata overload for a trimmed/AOT client. Avoid `ReadAsStringAsync`
or browser `response.json()` when incremental delivery is wanted: both collect
the complete response.

NDJSON uses `application/x-ndjson`. Each value is compact JSON followed by LF;
there is no surrounding array. An empty sequence has an empty body. Read lines
with `StreamReader.ReadLineAsync(cancellationToken)` and deserialize each line,
or use a browser `fetch()` readable stream. Network chunks can split a UTF-8
character, record or newline; decode incrementally and retain incomplete lines.
Do not treat an individual network chunk as a record.

## Limits, flushes and lifetime

`JsonStreamOptions` is immutable:

| Option | Default | Meaning |
| --- | --- | --- |
| `maxItemBytes` | 1 MiB | Maximum serialized UTF-8 bytes in one record; allowed range 1 byte through 64 MiB. |
| `flushEvery` | 1 | Explicitly flush after this many records; successful completion always flushes. |
| `preferCompression` | false | Prefer identity for prompt delivery; normal content-coding negotiation still applies. |

Do not predeclare `Content-Length` for a streamed body; it is rejected before
enumeration. The source sequence is never collected or prefetched. Each item is completely
serialized before writing its delimiter and bytes, and every transport write
and configured flush is awaited before requesting another item. One reusable
record buffer grows only up to `maxItemBytes`; growing it temporarily holds the
old and new arrays. This is not a cap on objects supplied by the application,
serializer scratch space, custom converters or transport buffers. A single
large record must be serialized before any of its bytes can be sent.

Records are compact, even when the item context has `WriteIndented = true`.
NDJSON rejects literal CR/LF emitted by custom raw-value converters before
sending that record. Escaped newlines in ordinary JSON strings are supported.
Custom converters remain responsible for their JSON validity, allocations,
thread safety and synchronous execution time.

The result owns enumeration and the opened response stream, but does not
own/dispose the source object or item values. Enumerator cleanup is awaited
before sending the successful array ending/response completion. For these
explicit streaming return types, owned controllers stay alive until
the streaming task finishes, including producer cleanup. Existing non-streaming
return types retain legacy disposal timing. Context-aware registration retains
its explicit awaited release callback. Existing authorization
and hooks run before the stream starts. HEAD and statuses without response
entities do not enumerate the source.

The context token and an optional `WriteAsync` token are combined and passed
to `GetAsyncEnumerator`. Producers must cooperate with cancellation. The
context token is not a universal remote-disconnect signal; transport failures
stop enumeration on the next attempted write, and a producer blocked between
records may not observe a disconnect until it produces again. Use cancellable,
bounded waits in the producer. There is no background prefetch, heartbeat,
implicit idle deadline, replay store or automatic reconnection. Flushes cannot
force an intermediary or client to consume bytes immediately.

## Errors and client recovery

Source/serialization failures before the first record is ready use the normal
error pipeline, including configured problem details. A record that exceeds
its byte budget is an application/output error, not a request-body 413.
Once headers/body are committed, status and response format cannot be replaced.
The managed HTTP/1 connection is aborted without a complete body terminator;
HTTP/2 and HTTP/3 reset the affected stream. No error JSON or success terminator
is appended to the partial stream. Cleanup errors retain the primary error
when both fail.

Clients must handle transport/read failures. A partial JSON array is invalid.
Completed NDJSON lines remain parseable, but do not prove the operation finished
successfully. Applications requiring resumable or transactional delivery should
include item IDs/checkpoints and define their own completion/replay protocol.

## OpenAPI and verification

Generated controller metadata describes an array schema under `application/json`
for `JsonArrayStream<T>`. NDJSON describes one record under `application/x-ndjson`,
with `x-embedio-framing: ndjson`; both carry `x-embedio-streaming: true`. OpenAPI
3.1 has no universal NDJSON sequence schema, so clients must interpret that
framing explicitly rather than assuming the complete body is one object.
Explicit response declarations still take precedence; keep them consistent with
the handler. Reflection registrations retain the existing OpenAPI limitations.

Regression tests exercise generated/reflection results, progressive reads,
record budgets, cancellation, HEAD, compression and post-commit failures across
HTTP/1, HTTP/2 and HTTP/3. The `json-streaming` state-machine campaign uses three
concurrent actors, independent record/count oracles and enumerator lifetime
ledgers, with a checked-in replay. Native AOT smoke exercises generated array
and NDJSON controllers and their schemas. These checks do not establish an
endpoint latency percentile or a measured performance improvement.
