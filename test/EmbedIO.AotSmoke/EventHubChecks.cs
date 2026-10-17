using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke;

internal static class EventHubChecks
{
    internal static WebApiModule CreateModule(ServerSentEventHub hub) => new WebApiModule("/event-hub")
        .WithController(EventHubSmokeBindings.EventHubSmokeController, () => new EventHubSmokeController(hub));
    internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks, ServerSentEventHub hub)
    {
        using var response = await client.GetAsync(url + "event-hub/events", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        if (await reader.ReadLineAsync() != ": connected" || await reader.ReadLineAsync() != "") throw new InvalidOperationException("Missing AOT SSE handshake.");
        var id = hub.Publish("live", "progress"); hub.Complete();
        var body = await reader.ReadToEndAsync();
        if (body != "id: " + id + "\nevent: progress\ndata: live\n\n") throw new InvalidOperationException("AOT SSE framing mismatch.");
        var doc = new OpenApiDocumentBuilder("Events", "1").AddController("/event-hub", EventHubSmokeBindings.EventHubSmokeController).Build(SmokeJson.Default.Options);
        if (doc.Root["paths"]?["/event-hub/events"]?["get"]?["responses"]?["200"]?["content"]?["text/event-stream"] == null)
            throw new InvalidOperationException("Missing AOT SSE metadata.");
        checks["event_hub"] = "generated Task<SSE> controller, bounded hub, progressive live event, completion and OpenAPI";
    }
}
[GeneratedControllerBinding(typeof(EventHubSmokeController), JsonSerializerContext = typeof(SmokeJson))]
internal static partial class EventHubSmokeBindings { }
internal sealed class EventHubSmokeController : WebApiController
{
    private readonly ServerSentEventHub _hub;
    internal EventHubSmokeController(ServerSentEventHub hub) => _hub = hub;
    [Route(HttpVerbs.Get, "/events")]
    public Task<ServerSentEventStream> Events() => Task.FromResult(_hub.Stream());
}
