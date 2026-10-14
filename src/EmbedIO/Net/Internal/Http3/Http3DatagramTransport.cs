#if NET10_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    internal enum Http3DatagramSendStatus { Queued, Unavailable, TooLarge, QueueFull, Closed }

    // Packet storage is owned until disposal. Routing slices the borrowed view;
    // it does not copy a native receive buffer a second time.
    internal sealed class Http3DatagramPacket : IDisposable
    {
        private IDisposable? _owner;
        private ReadOnlyMemory<byte> _payload;
        internal Http3DatagramPacket(ReadOnlyMemory<byte> payload, IDisposable owner)
        { _payload = payload; _owner = owner ?? throw new ArgumentNullException(nameof(owner)); }
        internal ReadOnlyMemory<byte> Payload => Volatile.Read(ref _owner) != null
            ? _payload : throw new ObjectDisposedException(nameof(Http3DatagramPacket));
        internal void StripHeader(int length) => _payload = Payload.Slice(length);
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
    }

    // HTTP/3 application datagrams over the native MsQuic connection: native send
    // states map to HTTP/3 ones and received packets keep their native storage.
    internal sealed class Http3DatagramTransport
    {
        private readonly MsQuicNativeDatagrams _datagrams;
        internal Http3DatagramTransport(MsQuicNativeDatagrams datagrams) => _datagrams = datagrams;
        internal int MaximumSendLength => _datagrams.MaxSendLength;
        internal Http3DatagramSendStatus TrySend(ReadOnlyMemory<byte> payload)
            => _datagrams.TrySend(payload, out _) switch
            {
                MsQuicDatagramSendStatus.Queued => Http3DatagramSendStatus.Queued,
                MsQuicDatagramSendStatus.Unavailable => Http3DatagramSendStatus.Unavailable,
                MsQuicDatagramSendStatus.TooLarge => Http3DatagramSendStatus.TooLarge,
                MsQuicDatagramSendStatus.QueueFull => Http3DatagramSendStatus.QueueFull,
                _ => Http3DatagramSendStatus.Closed,
            };
        internal async ValueTask<Http3DatagramPacket?> ReceiveAsync(CancellationToken token)
        {
            var packet = await _datagrams.ReceiveAsync(token).ConfigureAwait(false);
            return packet == null ? null : new Http3DatagramPacket(packet.Payload, packet);
        }
    }
}
#endif
