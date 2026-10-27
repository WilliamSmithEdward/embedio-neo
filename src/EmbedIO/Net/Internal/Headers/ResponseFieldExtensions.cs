namespace EmbedIO.Net.Internal
{
    // Writes a response's fields as its Headers would. A response the engine made takes them
    // in its field list, so the engine's own writes do not build the WebHeaderCollection; any
    // other implementation of IHttpResponse takes them through Headers.
    internal static class ResponseFieldExtensions
    {
        internal static void AddField(this IHttpResponse response, string name, string? value)
        {
            if (Fields(response) is { } fields) fields.Add(name, value);

            else response.Headers.Add(name, value);
        }

        internal static void SetField(this IHttpResponse response, string name, string? value)
        {
            if (Fields(response) is { } fields) fields.Set(name, value);

            else response.Headers.Set(name, value);
        }

        internal static void RemoveField(this IHttpResponse response, string name)
        {
            if (Fields(response) is { } fields) fields.Remove(name);

            else response.Headers.Remove(name);
        }

        private static ResponseHeaderFields? Fields(IHttpResponse response) =>
            response switch
            {
                HttpListenerResponse http1 => http1.Fields,
                MultiplexedResponse multiplexed => multiplexed.Fields,
                _ => null,
            };
    }
}
