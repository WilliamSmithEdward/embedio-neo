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
}
