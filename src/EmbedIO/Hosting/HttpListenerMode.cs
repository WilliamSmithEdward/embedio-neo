namespace EmbedIO
{
    /// <summary>
    /// Defines the HTTP listeners available for use in a <see cref="WebServer"/>.
    /// </summary>
    public enum HttpListenerMode
    {
        /// <summary>
        /// Use EmbedIO's internal HTTP/1.1 and HTTP/2 TCP engine.
        /// TLS negotiation capabilities depend on the selected target and runtime.
        /// </summary>
        EmbedIO = 0,

        /// <summary>
        /// Use the managed HTTP/3 engine over QUIC and TLS 1.3. Requires the .NET 10
        /// asset in a 64-bit process on Windows 11, Windows Server 2022 or later, Linux or
        /// macOS (Linux and macOS also need the EmbedIO.Quic native TLS runtime), and an
        /// HTTPS certificate with its private key.
        /// This mode listens on UDP; it does not accept HTTP/1 or HTTP/2 TCP connections.
        /// </summary>
        EmbedIOHttp3 = 2,

        /// <summary>
        /// Host HTTP/1.1 and HTTP/2 over TCP alongside HTTP/3 over QUIC on the
        /// same HTTPS prefixes. Has the same platform requirements as EmbedIOHttp3
        /// and needs a certificate with its private key. Supports bounded graceful drain across
        /// accepted TCP and QUIC requests.
        /// </summary>
        EmbedIOCombined = 3
    }
}
