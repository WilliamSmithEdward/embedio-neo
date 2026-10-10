using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal
{
    // Application-facing operations shared by the HTTP/2 and HTTP/3 transports.
    // Framing, compression, flow control, and stream ownership stay in each driver.
    internal interface IMultiplexedExchange
    {
        Http2RequestHeaders Request { get; }
        Stream InputStream { get; }
        CancellationToken CancellationToken { get; }
        Version ProtocolVersion { get; }
        bool InitialBodyComplete { get; }
        bool Ended { get; }
        bool CloseConnectionAfterResponse { get; set; }
        Task SendHeadersAsync(HpackField[] fields, bool endStream, CancellationToken token);
        Task WriteAsync(byte[] bytes, int offset, int count, bool endStream, CancellationToken token);
        Task CompleteAsync(CancellationToken token);
    }

    // Reserve before final headers so a declared body does not end the stream
    // before the application's ending field section can be written.
    internal interface IMultiplexedResponseTrailers
    {
        void ExpectTrailers();
        Task SendTrailersAsync(HpackField[] fields, CancellationToken token);
    }

    // A negotiated carrier failure ends one request stream, not its connection.
    internal interface IMultiplexedTunnelControl
    {
        Task AbortTunnelAsync(Exception cause, bool malformed);
    }

    // Optional: a transport that can send final response headers together with
    // the first body bytes. Explicit flushes still send headers on their own.
    internal interface IMultiplexedHeaderCoalescing
    {
        // True once final headers committed, even if the body write then failed.
        bool FinalHeadersSent { get; }
        Task SendHeadersAndWriteAsync(HpackField[] fields, byte[] bytes, int offset, int count, CancellationToken token);
    }
}
