using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    // The Retry packet (RFC 9000 17.2.5) and its integrity tag (RFC 9001 5.8): AES-128-GCM
    // with the version 1 key and nonce, over no plaintext, authenticating the original
    // destination ID followed by the packet.
    internal static class RetryPacket
    {
        private static readonly byte[] Key = Convert.FromHexString("be0c690b9f66575a1d766b54e368c84e");
        private static readonly byte[] Nonce = Convert.FromHexString("461599d35d632bf2239825bb");
        internal const int TagLength = 16;

        internal static byte[] Create(
            ReadOnlySpan<byte> clientSource,
            ReadOnlySpan<byte> retrySource,
            ReadOnlySpan<byte> token,
            ReadOnlySpan<byte> originalDestination
        )
        {
            var length = 1 + 4 + 1 + clientSource.Length + 1 + retrySource.Length + token.Length;
            var packet = new byte[length + TagLength];
            // Long header, type 3 (Retry); the four unused bits are zero.
            packet[0] = 0xf0;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), 1);
            var offset = 5;
            packet[offset++] = (byte)clientSource.Length;
            clientSource.CopyTo(packet.AsSpan(offset));
            offset += clientSource.Length;
            packet[offset++] = (byte)retrySource.Length;
            retrySource.CopyTo(packet.AsSpan(offset));
            offset += retrySource.Length;
            token.CopyTo(packet.AsSpan(offset));
            Tag(packet.AsSpan(0, length), originalDestination, packet.AsSpan(length));
            return packet;
        }

        internal static void Tag(ReadOnlySpan<byte> retry, ReadOnlySpan<byte> originalDestination, Span<byte> tag)
        {
            var pseudo = new byte[1 + originalDestination.Length + retry.Length];
            pseudo[0] = (byte)originalDestination.Length;
            originalDestination.CopyTo(pseudo.AsSpan(1));
            retry.CopyTo(pseudo.AsSpan(1 + originalDestination.Length));
            using var aead = new AesGcm(Key, TagLength);
            aead.Encrypt(Nonce, [], [], tag, pseudo);
        }
    }
}
