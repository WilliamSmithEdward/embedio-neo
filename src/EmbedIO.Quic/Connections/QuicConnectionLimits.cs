using System;

namespace EmbedIO.Quic
{
    internal sealed record QuicConnectionLimits
    {
        internal int InboundBidirectionalStreams { get; init; } = 16;
        internal int InboundUnidirectionalStreams { get; init; } = 16;
        internal int IdleTimeoutMilliseconds { get; init; } = 10000;

        // Frames share packets, 1-RTT acknowledgments wait briefly for a packet to share, and
        // stream credit goes out with other frames. The listener binding turns this on. Off, as
        // the engine's packet-level tests expect, every frame has its own packet and is sent as
        // soon as it is queued.
        internal bool SharePackets { get; init; }

        internal void Validate()
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(InboundBidirectionalStreams, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(InboundBidirectionalStreams, 1024);
            ArgumentOutOfRangeException.ThrowIfLessThan(InboundUnidirectionalStreams, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(InboundUnidirectionalStreams, 1024);
            ArgumentOutOfRangeException.ThrowIfLessThan(IdleTimeoutMilliseconds, 1);
        }
    }
}
