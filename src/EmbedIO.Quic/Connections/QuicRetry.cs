namespace EmbedIO.Quic
{
    // A connection admitted after Retry (RFC 9000 8.1.2): the destination ID of the
    // client's first Initial, which the server's transport parameters must repeat.
    internal sealed record QuicRetry(byte[] OriginalDestinationId);
}
