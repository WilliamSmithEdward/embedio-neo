internal sealed record Scenario(
    string Name,
    Protocol Protocol,
    bool Tls,
    string Path,
    int UploadBytes,
    int Connections,
    int Streams,
    int Pipeline,
    int RequestsPerConnection,
    string Description,
    double MaxSeconds = double.MaxValue)
{
    internal const int Mebibyte = 1 << 20;

    // HTTP/1.1 compares the candidate with the current main core and Kestrel. Main has
    // no HTTP/2 or HTTP/3, so those scenarios compare the candidate with Kestrel only.
    internal bool BaselineApplies => Protocol == Protocol.Http1;

    internal static IReadOnlyList<Scenario> All { get; } =
    [
        new("h1-plain-small-c64", Protocol.Http1, false, "/plaintext", 0, 64, 1, 1, 0, "13-byte GET, 64 keep-alive connections"),
        new("h1-plain-small-c256", Protocol.Http1, false, "/plaintext", 0, 256, 1, 1, 0, "13-byte GET, 256 keep-alive connections"),
        new("h1-plain-pipe16-c16", Protocol.Http1, false, "/plaintext", 0, 16, 1, 16, 0, "13-byte GET pipelined 16 deep, 16 connections"),
        // Controls: the client sends Connection: close on every 100th request, so every
        // engine has the same connection lifetime as EmbedIO's per-connection cap and
        // the server closes first. Separates reconnect cost from per-request cost.
        new("h1-plain-small-c64-close100", Protocol.Http1, false, "/plaintext", 0, 64, 1, 1, 100, "control: 13-byte GET, 64 connections, closed after 100 requests"),
        new("h1-plain-upload1m-c16-close100", Protocol.Http1, false, "/upload", Mebibyte, 16, 1, 1, 100, "control: 1 MiB POST, 16 connections, closed after 100 requests"),
        new("h1-plain-churn-c64", Protocol.Http1, false, "/plaintext", 0, 64, 1, 1, 1, "one request per connection (Connection: close), 64 concurrent; 5 s cap (host TIME_WAIT capacity)", MaxSeconds: 5),
        new("h1-plain-large1m-c16", Protocol.Http1, false, "/bytes/1048576", 0, 16, 1, 1, 0, "1 MiB Content-Length response, 16 connections"),
        new("h1-plain-upload1m-c16", Protocol.Http1, false, "/upload", Mebibyte, 16, 1, 1, 0, "1 MiB POST validated by the server, 16 connections"),
        new("h1-plain-stream1m-c16", Protocol.Http1, false, "/stream/1048576/16384", 0, 16, 1, 1, 0, "1 MiB chunked response flushed every 16 KiB, 16 connections"),
        new("h1-tls-small-c64", Protocol.Http1, true, "/plaintext", 0, 64, 1, 1, 0, "TLS 13-byte GET, 64 keep-alive connections"),
        new("h1-tls-churn-c32", Protocol.Http1, true, "/plaintext", 0, 32, 1, 1, 1, "TLS handshake plus one request per connection, 32 concurrent; 5 s cap (host TIME_WAIT capacity)", MaxSeconds: 5),
        new("h1-tls-large1m-c16", Protocol.Http1, true, "/bytes/1048576", 0, 16, 1, 1, 0, "TLS 1 MiB response, 16 connections"),
        new("h1-tls-upload1m-c16", Protocol.Http1, true, "/upload", Mebibyte, 16, 1, 1, 0, "TLS 1 MiB POST, 16 connections"),
        new("h2-plain-small-c8x32", Protocol.Http2, false, "/plaintext", 0, 8, 32, 1, 0, "h2c prior knowledge, 8 connections x 32 streams"),
        new("h2-tls-small-c8x32", Protocol.Http2, true, "/plaintext", 0, 8, 32, 1, 0, "h2 over TLS ALPN, 8 connections x 32 streams"),
        new("h2-tls-small-c64x1", Protocol.Http2, true, "/plaintext", 0, 64, 1, 1, 0, "h2 over TLS, 64 connections x 1 stream"),
        // The HTTP/2 client closes first, so each connection holds a client port in
        // TIME_WAIT. Warmup and measurement are each capped at 1.5 s (about 7,000
        // connections in total) to stay inside a 16,384-port dynamic range.
        new("h2-tls-churn-c32", Protocol.Http2, true, "/plaintext", 0, 32, 1, 1, 1, "TLS + h2 connection setup per request, 32 concurrent; 1.5 s cap (ephemeral ports)", MaxSeconds: 1.5),
        new("h2-tls-large1m-c4x4", Protocol.Http2, true, "/bytes/1048576", 0, 4, 4, 1, 0, "h2 1 MiB responses, 4 connections x 4 streams"),
        new("h2-tls-upload1m-c4x4", Protocol.Http2, true, "/upload", Mebibyte, 4, 4, 1, 0, "h2 1 MiB uploads, 4 connections x 4 streams"),
        new("h2-tls-stream1m-c4x4", Protocol.Http2, true, "/stream/1048576/16384", 0, 4, 4, 1, 0, "h2 1 MiB flushed every 16 KiB, 4 connections x 4 streams"),
        new("h3-small-c8x32", Protocol.Http3, true, "/plaintext", 0, 8, 32, 1, 0, "HTTP/3, 8 connections x 32 streams"),
        new("h3-churn-c16", Protocol.Http3, true, "/plaintext", 0, 16, 1, 1, 1, "QUIC handshake per request, 16 concurrent"),
        new("h3-large1m-c4x4", Protocol.Http3, true, "/bytes/1048576", 0, 4, 4, 1, 0, "HTTP/3 1 MiB responses, 4 connections x 4 streams"),
        new("h3-upload1m-c4x4", Protocol.Http3, true, "/upload", Mebibyte, 4, 4, 1, 0, "HTTP/3 1 MiB uploads, 4 connections x 4 streams"),
        new("h3-stream1m-c4x4", Protocol.Http3, true, "/stream/1048576/16384", 0, 4, 4, 1, 0, "HTTP/3 1 MiB flushed every 16 KiB, 4 connections x 4 streams"),
    ];
}
