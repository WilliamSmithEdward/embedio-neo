namespace EmbedIO
{
    /// <summary>
    /// Defines the HTTP listeners available for use in a <see cref="WebServer"/>.
    /// </summary>
    public enum HttpListenerMode
    {
        /// <summary>
        /// Use EmbedIO's internal HTTP listener implementation,
        /// based on Mono's <c>System.Net.HttpListener</c>.
        /// </summary>
        EmbedIO = 0,

        /// <summary>
        /// Use the managed HTTP/3 engine over QUIC and TLS 1.3. Requires the .NET 10
        /// asset, native QUIC support and an HTTPS certificate with its private key.
        /// This mode listens on UDP; it does not accept HTTP/1 or HTTP/2 TCP connections.
        /// </summary>
        EmbedIOHttp3 = 2,

        /// <summary>
        /// Host HTTP/1.1 and HTTP/2 over TCP alongside HTTP/3 over QUIC on the
        /// same HTTPS prefixes. Requires the .NET 10 asset, native QUIC support
        /// and a certificate with its private key. Supports bounded graceful drain across
        /// accepted TCP and QUIC requests.
        /// </summary>
        EmbedIOCombined = 3,
    }
}
