#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    // The kinds of QUIC stream HTTP/3 opens and accepts.
    internal enum Http3StreamType
    {
        Unidirectional,
        Bidirectional
    }

    // The directions of a QUIC stream an abort ends.
    [Flags]
    internal enum Http3AbortDirection
    {
        Read = 1,
        Write = 2,
        Both = Read | Write
    }

    // Ownership boundary between the HTTP/3 layer and the QUIC engine. Streams
    // returned by a connection are owned by the protocol worker. Writes borrow memory
    // only until completion.
    internal abstract class Http3TransportConnection : IAsyncDisposable
    {
        internal virtual Http3DatagramTransport? Datagrams => null;
        internal virtual ulong PeerMaximumDatagramFrameSize => 0;
        internal virtual bool ReliableReset => false;

        internal abstract ValueTask<Http3TransportStream> OpenOutboundStreamAsync(
            Http3StreamType type,
            CancellationToken token
        );

        internal abstract ValueTask<Http3TransportStream> AcceptInboundStreamAsync(CancellationToken token);

        // Closes the connection with the code, and completes once the close is queued, or at
        // once when the connection has already closed. The transport then spends its closing or
        // draining period (RFC 9000 section 10.2), which WaitClosedAsync waits for.
        internal abstract ValueTask CloseAsync(long code, CancellationToken token);

        internal abstract Task WaitClosedAsync(CancellationToken token);

        public abstract ValueTask DisposeAsync();
    }

    internal abstract class Http3TransportStream : Stream
    {
        internal abstract long Id { get; }
        internal abstract Http3StreamType Type { get; }
        internal abstract Task ReadsClosed { get; }
        internal abstract Task WritesClosed { get; }

        internal abstract void Abort(Http3AbortDirection direction, long code);

        internal abstract void CompleteWrites();

        internal virtual void SetReliablePrefix(ReadOnlySpan<byte> prefix) =>
            throw new NotSupportedException("The transport cannot retain a reliable stream prefix.");

        internal virtual void EnableApplicationBodyCancellation(CancellationToken requestCancellation) { }

        // A request stream keeps its place against the peer's stream limit until its worker
        // disposes it, so requests still in the application count against that limit.
        internal virtual void RetainUntilDisposed() { }

        internal abstract ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            bool completeWrites,
            CancellationToken token
        );
    }
}
#endif
