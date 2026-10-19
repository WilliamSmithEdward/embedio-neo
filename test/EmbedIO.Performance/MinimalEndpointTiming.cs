using EmbedIO;
using EmbedIO.Endpoints;

internal static class MinimalEndpointTiming
{
    internal static void Configure(WebServer server, string scenario)
    {
        var endpoints = server.GetEndpointModule();

        if (scenario == "minimal-reflection")
        {
            endpoints.Root.MapGet("/__control", MinimalLatencyHandlers.Control);
            endpoints.Root.MapGet("/target", MinimalLatencyHandlers.Target);
        }

        else
        {
            endpoints.Root.MapGet("/__control", MinimalLatencyHandlers.ControlEndpoint);

            if (scenario == "minimal-parameter")
                endpoints.Root.MapGet("/target/{id}", MinimalLatencyHandlers.TargetIdEndpoint);

            else endpoints.Root.MapGet("/target", MinimalLatencyHandlers.TargetEndpoint);
        }

        endpoints.Compile();
    }
}

[GeneratedEndpointBinding]
internal static partial class MinimalLatencyHandlers
{
    public static Task Control(IHttpContext context) => EndpointLatency.ReplyAsync(context, "ok");

    public static Task Target(IHttpContext context) => EndpointLatency.ReplyAsync(context, "ok");

    public static Task TargetId(IHttpContext context, int id) =>
        id == 42 ? EndpointLatency.ReplyAsync(context, "ok") : throw new InvalidOperationException("Wrong bound ID.");
}
