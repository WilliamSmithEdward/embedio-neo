using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    // The Initial packet a server sends to refuse a connection (RFC 9000 5.2.2): one
    // CONNECTION_CLOSE with CONNECTION_REFUSED, protected with the Initial keys that the
    // client's chosen destination ID derives (RFC 9001 5.2), so the client can read it
    // without any connection state on this side.
    internal static class ConnectionRefusal
    {
        internal const ulong ConnectionRefused = 0x02;

        // Null when the datagram does not start with a version 1 Initial header.
        internal static byte[]? Create(ReadOnlySpan<byte> initial, ulong error = ConnectionRefused)
        {
            if (
                initial.Length < 7
                || (initial[0] & 0xf0) != 0xc0
                || BinaryPrimitives.ReadUInt32BigEndian(initial.Slice(1, 4)) != 1
            )
                return null;

            var destinationLength = initial[5];

            if (destinationLength > 20 || initial.Length < 7 + destinationLength) return null;

            var destination = initial.Slice(6, destinationLength);
            var sourceLength = initial[6 + destinationLength];

            if (sourceLength > 20 || initial.Length < 7 + destinationLength + sourceLength) return null;

            var source = initial.Slice(7 + destinationLength, sourceLength);
            using var header = new MemoryStream();
            // Long header, Initial, four-byte packet number; the client's source ID is the destination.
            header.WriteByte(0xc3);
            header.Write([0, 0, 0, 1]);
            header.WriteByte((byte)source.Length);
            header.Write(source);
            Span<byte> id = stackalloc byte[8];
            RandomNumberGenerator.Fill(id);
            header.WriteByte((byte)id.Length);
            header.Write(id);
            header.WriteByte(0);
            // Padded to 1200 bytes like every Initial this server sends. The client's Initial
            // was at least as large, so the reply stays within the amplification limit.
            var payload = Wire.Frame(0x1c, error, 0, 0);
            Array.Resize(ref payload, 1200 - (int)header.Length - 2 - 4 - 16);
            Wire.Integer(header, (ulong)(4 + payload.Length + 16));
            var numberOffset = (int)header.Length;
            header.Write([0, 0, 0, 0]);
            using var keys = PacketKeys.Initial(destination, true);
            return keys.Protect(header.ToArray(), numberOffset, 0, payload);
        }
    }
}
