using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Reports the outcome of a nonblocking HTTP datagram submission.</summary>
    public enum HttpDatagramSendStatus
    {
        /// <summary>Submitted to the transport; delivery and acknowledgment are not guaranteed.</summary>
        Queued,
        /// <summary>The peer or transport has not negotiated datagram support.</summary>
        Unavailable,
        /// <summary>The payload exceeds the current wire limit, including its stream identifier.</summary>
        TooLarge,
        /// <summary>The bounded transport send queue is full.</summary>
        QueueFull,
        /// <summary>The tunnel's send direction or its owning channel has closed.</summary>
        Closed,
    }
    /// <summary>Exchanges unreliable HTTP datagrams for one explicitly accepted tunnel.</summary>
    /// <remarks>Packets may be lost, reordered or duplicated. Reliable capsules remain a separate mechanism.</remarks>
    public sealed class HttpDatagramChannel
    {
        private readonly Func<CancellationToken, ValueTask<HttpDatagram?>> _receive;
        private readonly Func<ReadOnlyMemory<byte>, HttpDatagramSendStatus> _send;
        private readonly Func<bool> _canSend;
        private readonly Func<int> _maximumPayload;
        private readonly Action _close;
        private int _closed;
        private int _cleanupStarted;
        internal HttpDatagramChannel(Func<CancellationToken, ValueTask<HttpDatagram?>> receive,
            Func<ReadOnlyMemory<byte>, HttpDatagramSendStatus> send, Func<bool> canSend,
            Func<int> maximumPayload, Action close)
        { _receive = receive; _send = send; _canSend = canSend; _maximumPayload = maximumPayload; _close = close; }
        /// <summary>Gets whether outgoing datagrams are negotiated and the send direction is open.</summary>
        /// <remarks>Incoming delivery can remain open when this is false.</remarks>
        public bool CanSend => Volatile.Read(ref _closed) == 0 && _canSend();
        /// <summary>Gets the current payload limit after allowing for the stream identifier.</summary>
        /// <remarks>The limit can shrink. Zero can allow an empty packet; check CanSend separately.</remarks>
        public int MaximumPayloadLength => Volatile.Read(ref _closed) == 0 ? _maximumPayload() : 0;
        /// <summary>Receives one owned packet, or null when incoming delivery ends.</summary>
        /// <param name="cancellationToken">Cancels this receive without discarding unread packets.</param>
        /// <returns>A packet that the caller must dispose. An empty payload is distinct from null.</returns>
        public ValueTask<HttpDatagram?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Volatile.Read(ref _closed) == 0 ? _receive(cancellationToken) : new ValueTask<HttpDatagram?>((HttpDatagram?)null);
        }
        /// <summary>Submits a borrowed payload without waiting for delivery.</summary>
        /// <param name="payload">Bytes copied by the transport before this method returns.</param>
        /// <returns>The submission result. Queued is not a delivery guarantee.</returns>
        public HttpDatagramSendStatus TrySend(ReadOnlyMemory<byte> payload)
            => Volatile.Read(ref _closed) == 0 ? _send(payload) : HttpDatagramSendStatus.Closed;
        internal void BeginClose() => Interlocked.Exchange(ref _closed, 1);
        internal void Close()
        {
            BeginClose();
            if (Interlocked.Exchange(ref _cleanupStarted, 1) == 0) _close();
        }
    }
}
