using System;
using System.IO;

namespace EmbedIO.Net.Internal.Http3
{
    // RFC 9297 section 2.1. The caller retains ownership of the packet; parsing
    // returns a borrowed payload offset and never copies datagram contents.
    internal static class Http3DatagramCodec
    {
        internal static long ReadHeader(byte[] packet, int offset, int count, out int payloadOffset)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (offset < 0 || count < 0 || offset > packet.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            var cursor = offset;
            long quarter;
            try { quarter = QuicInteger.Read(packet, ref cursor, offset + count); }
            catch (EndOfStreamException)
            { throw new Http3ProtocolException(0x33, "Truncated HTTP/3 datagram stream identifier."); }
            if (quarter >= 1L << 60)
                throw new Http3ProtocolException(0x33, "HTTP/3 datagram stream identifier exceeds the QUIC stream limit.");
            payloadOffset = cursor;
            return quarter << 2;
        }

        internal static int WriteHeader(byte[] packet, int offset, long streamId)
        {
            if (streamId < 0 || streamId > QuicInteger.Maximum || (streamId & 3) != 0)
                throw new ArgumentOutOfRangeException(nameof(streamId), "Datagrams require a client-initiated bidirectional request stream.");
            return QuicInteger.Write(packet, offset, streamId >> 2);
        }
    }
}
