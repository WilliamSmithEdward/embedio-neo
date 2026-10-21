namespace EmbedIO.Quic
{
    internal sealed record QuicConnectionLimits
    {
        internal int InboundBidirectionalStreams { get; init; } = 16;
        internal int InboundUnidirectionalStreams { get; init; } = 16;
        internal int IdleTimeoutMilliseconds { get; init; } = 10000;

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
