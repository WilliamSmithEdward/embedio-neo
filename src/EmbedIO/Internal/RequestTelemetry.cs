namespace EmbedIO.Internal
{
    // Implemented by engine contexts. Telemetry is non-null only while the owning server
    // records telemetry, so routing pays for template tracking only when it is enabled.
    internal interface ITelemetryContext
    {
        RequestTelemetry? Telemetry { get; set; }
    }

    // Per-request telemetry state shared with routing: the matched route template, built
    // from module base routes and the resolver's route as dispatch descends.
    internal sealed partial class RequestTelemetry
    {
        internal string? Route { get; set; }

        internal static RequestTelemetry? Of(IHttpContext context)
            => context is ITelemetryContext owner ? owner.Telemetry : null;

        // Joins a base route ("/api/") and a route relative to it ("/items/{id}").
        internal static string Combine(string? prefix, string route)
        {
            if (prefix == null || prefix.Length == 0 || prefix == "/") return route;
            if (route == "/") return prefix;
            return prefix[prefix.Length - 1] == '/' ? prefix.Substring(0, prefix.Length - 1) + route : prefix + route;
        }
    }
}
