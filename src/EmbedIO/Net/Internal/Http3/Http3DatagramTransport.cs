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

    // Packet storage is owned until disposal. Routing slices the borrowed view;
    // it does not copy a native receive buffer a second time.
    internal sealed class Http3DatagramPacket : IDisposable
    {
        private IDisposable? _owner;
        private ReadOnlyMemory<byte> _payload;
        private int _disposed;

        internal Http3DatagramPacket(ReadOnlyMemory<byte> payload, IDisposable owner)
        {
            _payload = payload;
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

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
            Interlocked
                .Exchange(ref _owner, null)
                ?.Dispose();
        }
    }

    internal abstract class Http3DatagramTransport
    {
        internal abstract int MaximumSendLength { get; }

        internal abstract Http3DatagramSendStatus TrySend(ReadOnlyMemory<byte> payload);

        internal abstract ValueTask<Http3DatagramPacket?> ReceiveAsync(CancellationToken token);
    }

    // HTTP/3 application datagrams over the native MsQuic connection: native send
    // states map to HTTP/3 ones and received packets keep their native storage.
    internal sealed class MsQuicHttp3DatagramTransport : Http3DatagramTransport
    {
        private readonly MsQuicNativeDatagrams _datagrams;

        internal MsQuicHttp3DatagramTransport(MsQuicNativeDatagrams datagrams) => _datagrams = datagrams;

        internal override int MaximumSendLength => _datagrams.MaxSendLength;

        internal override Http3DatagramSendStatus TrySend(ReadOnlyMemory<byte> payload) => _datagrams.TrySend(
            payload,
            out _
        ) switch
        {
            MsQuicDatagramSendStatus.Queued => Http3DatagramSendStatus.Queued,
            MsQuicDatagramSendStatus.Unavailable => Http3DatagramSendStatus.Unavailable,
            MsQuicDatagramSendStatus.TooLarge => Http3DatagramSendStatus.TooLarge,
            MsQuicDatagramSendStatus.QueueFull => Http3DatagramSendStatus.QueueFull,
            _ => Http3DatagramSendStatus.Closed
        };

        internal override async ValueTask<Http3DatagramPacket?> ReceiveAsync(CancellationToken token)
        {
            var packet = await _datagrams
                .ReceiveAsync(token)
                .ConfigureAwait(false);
            return packet == null ? null : new Http3DatagramPacket(packet.Payload, packet);
        }
    }
}
#endif
