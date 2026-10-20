namespace EmbedIO.Quic
{
    internal sealed class TransportException(ulong errorCode, string message) : IOException(message)
    {
        internal ulong ErrorCode { get; } = errorCode;
    }
}
