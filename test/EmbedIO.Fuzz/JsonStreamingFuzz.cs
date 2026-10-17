using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;

internal static class JsonStreamingFuzz
{
    internal static async Task RunAsync(StateMachineTrace trace, CancellationToken token)
    {
        var controllers = new[] { new StreamingFuzzController(), new StreamingFuzzController() };
        using var reflection = new TestWebServer();
        using var generated = new TestWebServer();
        reflection.WithModule(new WebApiModule("/").WithController(() => controllers[0]));
        generated.WithModule(new WebApiModule("/").WithController(StreamingFuzzBindings.StreamingFuzzController, () => controllers[1]));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var runs = new[] { reflection.RunAsync(stop.Token), generated.RunAsync(stop.Token) };
        var expectedDisposals = 0;
        try
        {
            foreach (var step in trace.Steps)
                await Task.WhenAll(Enumerable.Range(0, 3).Select(actor => Check((int)((uint)step.Command % 6), (int)((uint)(step.Value + actor) % 16))));
            foreach (var controller in controllers)
                if (controller.Active != 0 || controller.Disposed != expectedDisposals)
                    throw new InvalidOperationException("Streaming lifetime ledger mismatch.");
        }
        finally { stop.Cancel(); await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(5)); }

        async Task Check(int mode, int count)
        {
            var head = mode == 5;
            if (!head) Interlocked.Increment(ref expectedDisposals);
            var ndjson = mode is 1 or 3;
            var path = (mode == 4 ? "limited" : ndjson ? "lines" : "array") + "/" + count;
            trace.Cover("stream-mode-" + mode);
            trace.Cover(count == 0 ? "empty" : "records");
            await Task.WhenAll(Send(reflection), Send(generated));
            async Task Send(TestWebServer server)
            {
                using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, "/" + path);
                using var response = await server.Client.SendAsync(request, token);
                var text = await response.Content.ReadAsStringAsync(token);
                if (mode == 4 && count > 0)
                {
                    if (response.StatusCode != HttpStatusCode.InternalServerError) throw new InvalidOperationException("First-record budget oracle mismatch.");
                    return;
                }
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"Streaming status mismatch: path={path}, generated={ReferenceEquals(server, generated)}, status={response.StatusCode}, body={text}");
                if (head) { if (text.Length != 0) throw new InvalidOperationException("HEAD body oracle mismatch."); return; }
                if (!ndjson)
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.GetArrayLength() != count) throw new InvalidOperationException("Array record count mismatch.");
                    for (var i = 0; i < count; i++) CheckItem(document.RootElement[i], i);
                }
                else
                {
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length != count || count > 0 && !text.EndsWith('\n')) throw new InvalidOperationException("NDJSON framing mismatch.");
                    for (var i = 0; i < count; i++) { using var item = JsonDocument.Parse(lines[i]); CheckItem(item.RootElement, i); }
                }
                static void CheckItem(JsonElement item, int i)
                {
                    if (item.GetProperty("Index").GetInt32() != i || item.GetProperty("Text").GetString() != "item\n\"\u00e9\ud83d\ude00" + i)
                        throw new InvalidOperationException("Independent record-value oracle mismatch.");
                }
            }
        }
    }
}

[GeneratedControllerBinding(typeof(StreamingFuzzController))]
internal static partial class StreamingFuzzBindings { }

internal sealed class StreamingFuzzController : WebApiController
{
    internal int Active;
    internal int Disposed;
    [Route(HttpVerbs.Get, "/array/{count}")]
    [Route(HttpVerbs.Head, "/array/{count}")]
    public JsonArrayStream<StreamingFuzzItem> Array(int count) => JsonStream.Array(Items(count), StreamingFuzzJson.Default.StreamingFuzzItem, new JsonStreamOptions(flushEvery: count % 4 + 1));
    [Route(HttpVerbs.Get, "/lines/{count}")]
    public Task<NdjsonStream<StreamingFuzzItem>> Lines(int count) => Task.FromResult(JsonStream.Ndjson(Items(count), StreamingFuzzJson.Default.StreamingFuzzItem));
    [Route(HttpVerbs.Get, "/limited/{count}")]
    public JsonArrayStream<StreamingFuzzItem> Limited(int count) => JsonStream.Array(Items(count), StreamingFuzzJson.Default.StreamingFuzzItem, new JsonStreamOptions(maxItemBytes: 1));
    private async IAsyncEnumerable<StreamingFuzzItem> Items(int count, [EnumeratorCancellation] CancellationToken token = default)
    {
        Interlocked.Increment(ref Active);
        try { for (var i = 0; i < count; i++) { token.ThrowIfCancellationRequested(); await Task.Yield(); yield return new StreamingFuzzItem { Index = i, Text = "item\n\"\u00e9\ud83d\ude00" + i }; } }
        finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Disposed); }
    }
}
internal sealed class StreamingFuzzItem { public int Index { get; set; } public string Text { get; set; } = string.Empty; }
[JsonSerializable(typeof(StreamingFuzzItem))]
internal sealed partial class StreamingFuzzJson : JsonSerializerContext { }
