#if NET10_0_OR_GREATER
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    // RFC 9297: unknown requests and receive-closed requests silently discard
    // datagrams. One connection budget bounds storage across all request queues.
    internal sealed class Http3DatagramRouter : IDisposable
    {
        private readonly object _gate = new();
        private readonly Http3DatagramTransport _transport;
        private readonly Dictionary<long, Http3DatagramChannel> _channels = new();
        private readonly int _maximumBytes;
        private readonly int _maximumPackets;
        private int _queuedBytes;
        private int _queuedPackets;
        private bool _peerEnabled;
        private bool _disposed;
        internal Http3DatagramRouter(Http3DatagramTransport transport, int maximumPackets = 128, int maximumBytes = 262144)
        {
            if (maximumPackets <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPackets));
            if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _maximumPackets = maximumPackets; _maximumBytes = maximumBytes;
        }
        internal void SetPeerEnabled(bool enabled) { lock (_gate) _peerEnabled = enabled; }
        internal Http3DatagramChannel Open(long streamId, Func<bool> canReceive, Func<bool> canSend)
        {
            if (streamId < 0 || streamId > QuicInteger.Maximum || (streamId & 3) != 0) throw new ArgumentOutOfRangeException(nameof(streamId));
            var channel = new Http3DatagramChannel(this, streamId, canReceive, canSend);
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Http3DatagramRouter));
                _channels.Add(streamId, channel);
            }
            return channel;
        }
        internal async Task RunAsync(CancellationToken token)
        {
            while (true)
            {
                var packet = await _transport.ReceiveAsync(token).ConfigureAwait(false);
                if (packet == null) return;
                Deliver(packet);
            }
        }
        internal void Deliver(Http3DatagramPacket packet)
        {
            var transferred = false;
            try
            {
                var id = Http3DatagramCodec.ReadPacketHeader(packet.Payload.Span, out var header);
                packet.StripHeader(header);
                lock (_gate)
                {
                    if (_disposed || !_peerEnabled || !_channels.TryGetValue(id, out var channel) || !channel.CanReceive()) return;
                    var length = packet.Payload.Length;
                    if (_queuedPackets == _maximumPackets || length > _maximumBytes - _queuedBytes) return;
                    if (!channel.Queue.Writer.TryWrite(packet)) return;
                    _queuedPackets++; _queuedBytes += length;
                    transferred = true;
                }
            }
            finally { if (!transferred) packet.Dispose(); }
        }
        internal async ValueTask<Http3DatagramPacket?> ReceiveAsync(Http3DatagramChannel channel, CancellationToken token)
        {
            while (await channel.Queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (!channel.Queue.Reader.TryRead(out var packet)) continue;
                    _queuedPackets--; _queuedBytes -= packet.Payload.Length;
                    if (channel.CanReceive()) return packet;
                    packet.Dispose();
                }
            }
            return null;
        }
        internal Http3DatagramSendStatus TrySend(Http3DatagramChannel channel, ReadOnlyMemory<byte> payload)
        {
            lock (_gate)
            {
                if (_disposed || !_channels.TryGetValue(channel.Id, out var current) || !ReferenceEquals(current, channel) || !channel.CanSend()) return Http3DatagramSendStatus.Closed;
                if (!_peerEnabled) return Http3DatagramSendStatus.Unavailable;
                var headerBytes = QuicInteger.EncodedLength(channel.Id >> 2);
                var maximum = _transport.MaximumSendLength;
                if (maximum == 0) return Http3DatagramSendStatus.Unavailable;
                if (payload.Length > maximum - headerBytes) return Http3DatagramSendStatus.TooLarge;
                var packet = ArrayPool<byte>.Shared.Rent(headerBytes + payload.Length);
                try
                {
                    Http3DatagramCodec.WriteHeader(packet, 0, channel.Id);
                    payload.CopyTo(packet.AsMemory(headerBytes));
                    // The transport copies before returning, including failure.
                    return _transport.TrySend(packet.AsMemory(0, headerBytes + payload.Length));
                }
                finally { ArrayPool<byte>.Shared.Return(packet, true); }
            }
        }
        internal bool IsSendAvailable(Http3DatagramChannel channel)
        {
            lock (_gate)
                return !_disposed && _channels.TryGetValue(channel.Id, out var current) && ReferenceEquals(current, channel)
                    && channel.CanSend() && _peerEnabled && _transport.MaximumSendLength >= QuicInteger.EncodedLength(channel.Id >> 2);
        }
        internal int MaximumPayloadLength(Http3DatagramChannel channel)
        {
            lock (_gate)
                return IsSendAvailable(channel) ? Math.Max(0, _transport.MaximumSendLength - QuicInteger.EncodedLength(channel.Id >> 2)) : 0;
        }
        internal void Close(Http3DatagramChannel channel)
        {
            lock (_gate)
            {
                if (!_channels.TryGetValue(channel.Id, out var current) || !ReferenceEquals(current, channel)) return;
                _channels.Remove(channel.Id);
                CompleteReceive(channel);
            }
        }
        internal void CompleteReceive(Http3DatagramChannel channel)
        {
            lock (_gate)
            {
                channel.Queue.Writer.TryComplete();
                while (channel.Queue.Reader.TryRead(out var packet))
                { _queuedPackets--; _queuedBytes -= packet.Payload.Length; packet.Dispose(); }
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var channel in new List<Http3DatagramChannel>(_channels.Values)) Close(channel);
            }
        }
    }
    internal sealed class Http3DatagramChannel : IDisposable
    {
        private readonly Http3DatagramRouter _owner;
        internal readonly long Id;
        internal readonly Func<bool> CanReceive;
        internal readonly Func<bool> CanSend;
        internal readonly Channel<Http3DatagramPacket> Queue = Channel.CreateBounded<Http3DatagramPacket>(16);
        internal Http3DatagramChannel(Http3DatagramRouter owner, long id, Func<bool> canReceive, Func<bool> canSend)
        {
            _owner = owner; Id = id;
            CanReceive = canReceive ?? throw new ArgumentNullException(nameof(canReceive));
            CanSend = canSend ?? throw new ArgumentNullException(nameof(canSend));
        }
        internal ValueTask<Http3DatagramPacket?> ReceiveAsync(CancellationToken token) => _owner.ReceiveAsync(this, token);
        internal Http3DatagramSendStatus TrySend(ReadOnlyMemory<byte> payload) => _owner.TrySend(this, payload);
        internal bool IsSendAvailable => _owner.IsSendAvailable(this);
        internal int MaximumPayloadLength => _owner.MaximumPayloadLength(this);
        internal void CompleteReceive() => _owner.CompleteReceive(this);
        public void Dispose() => _owner.Close(this);
    }
}
#endif
