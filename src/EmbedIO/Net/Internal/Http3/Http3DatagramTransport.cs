#if NET10_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    internal enum Http3DatagramSendStatus
    {
        Queued,
        Unavailable,
        TooLarge,
        QueueFull,
        Closed
    }

    // The payload stays readable until disposal. Routing slices the received
    // array; it does not copy the payload a second time.
    internal sealed class Http3DatagramPacket : IDisposable
    {
        private ReadOnlyMemory<byte> _payload;
        private int _disposed;

        internal Http3DatagramPacket(byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            _payload = payload;
        }

        internal ReadOnlyMemory<byte> Payload =>
            Volatile.Read(ref _disposed) == 0
                ? _payload
                : throw new ObjectDisposedException(nameof(Http3DatagramPacket));

        internal void StripHeader(int length) => _payload = Payload.Slice(length);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _payload = default;
        }
    }

    internal abstract class Http3DatagramTransport
    {
        internal abstract int MaximumSendLength { get; }

        internal abstract Http3DatagramSendStatus TrySend(ReadOnlyMemory<byte> payload);

        internal abstract ValueTask<Http3DatagramPacket?> ReceiveAsync(CancellationToken token);
    }
}
#endif
