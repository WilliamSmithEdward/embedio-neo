using System.Text.Json;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke;

internal static class StreamingChecks
{
    internal static WebApiModule CreateModule() => new WebApiModule("/json-streams").WithController(StreamingSmokeBindings.StreamingSmokeController);
    internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
    {
        foreach (var path in new[] { "array", "lines" })
        {
            using var response = await client.GetAsync(url + "json-streams/" + path);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync();
            var expected = path == "array" ? "[\"one\",\"two\"]" : "\"one\"\n\"two\"\n";
            if (text != expected) throw new InvalidOperationException("AOT streaming mismatch: " + text);
        }
        var doc = new OpenApiDocumentBuilder("Streaming", "1").AddController("/json-streams", StreamingSmokeBindings.StreamingSmokeController).Build(SmokeJson.Default.Options);
        if (doc.Root["paths"]?["/json-streams/array"]?["get"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["type"]?.GetValue<string>() != "array")
            throw new InvalidOperationException("AOT streaming schema missing.");
        checks["json_streaming"] = "generated JSON array, NDJSON, item metadata and OpenAPI";
    }
}
[GeneratedControllerBinding(typeof(StreamingSmokeController), JsonSerializerContext = typeof(SmokeJson))]
internal static partial class StreamingSmokeBindings { }
internal sealed class StreamingSmokeController : WebApiController
{
    [Route(HttpVerbs.Get, "/array")]
    public JsonArrayStream<string> Array() => JsonStream.Array(Values(), SmokeJson.Default.String);
    [Route(HttpVerbs.Get, "/lines")]
    public Task<NdjsonStream<string>> Lines() => Task.FromResult(JsonStream.Ndjson(Values(), SmokeJson.Default.String));
    private static async IAsyncEnumerable<string> Values() { yield return "one"; await Task.Yield(); yield return "two"; }
}
