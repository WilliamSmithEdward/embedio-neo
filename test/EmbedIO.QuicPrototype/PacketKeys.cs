using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EmbedIO.QuicPrototype
{
    // Original implementation of RFC 9001 sections 5.1-5.4; AES primitives are BCL implementations.
    internal sealed class PacketKeys : IDisposable
    {
        private readonly AesGcm _aead;
        private readonly Aes _header;
        private readonly byte[] _iv;

        internal PacketKeys(ReadOnlySpan<byte> secret)
        {
            var key = Expand(secret, "quic key", 16);
            var headerKey = Expand(secret, "quic hp", 16);
            _iv = Expand(secret, "quic iv", 12);
            _aead = new AesGcm(key, 16);
            _header = Aes.Create();
            _header.Key = headerKey;
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(headerKey);
        }

        internal static byte[] Expand(ReadOnlySpan<byte> secret, string label, int count)
        {
            var name = Encoding.ASCII.GetBytes("tls13 " + label);
            var info = new byte[4 + name.Length];
            BinaryPrimitives.WriteUInt16BigEndian(info, checked((ushort)count));
            info[2] = checked((byte)name.Length);
            name.CopyTo(info, 3);
            var output = new byte[count];
            HKDF.Expand(HashAlgorithmName.SHA256, secret, output, info);
            return output;
        }

        internal static PacketKeys Initial(ReadOnlySpan<byte> destinationId, bool server)
        {
            var salt = Convert.FromHexString("38762cf7f55934b34d179ae6a4c80cadccbb7f0a");
            var initial = new byte[32];
            HKDF.Extract(HashAlgorithmName.SHA256, destinationId, salt, initial);
            var secret = Expand(initial, server ? "server in" : "client in", 32);

            try
            {
                return new PacketKeys(secret);
            }

            finally
            {
                CryptographicOperations.ZeroMemory(initial);
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        internal byte[] Protect(byte[] header, int numberOffset, ulong number, ReadOnlySpan<byte> plaintext)
        {
            var packet = new byte[header.Length + plaintext.Length + 16];
            header.CopyTo(packet, 0);
            Span<byte> nonce = stackalloc byte[12];
            Nonce(number, nonce);
            _aead.Encrypt(
                nonce,
                plaintext,
                packet.AsSpan(header.Length, plaintext.Length),
                packet.AsSpan(packet.Length - 16),
                header
            );
            Mask(packet, numberOffset, 4);
            return packet;
        }

        internal byte[] Unprotect(
            ReadOnlySpan<byte> protectedPacket,
            int numberOffset,
            ulong expected,
            out ulong number
        )
        {
            if (numberOffset < 0 || numberOffset + 20 > protectedPacket.Length)
                throw new InvalidDataException("Short protected packet.");

            var packet = protectedPacket.ToArray();
            Span<byte> mask = stackalloc byte[16];
            _header.EncryptEcb(packet.AsSpan(numberOffset + 4, 16), mask, PaddingMode.None);
            var longHeader = (packet[0] & 128) != 0;
            packet[0] ^= (byte)(mask[0] & (longHeader ? 15 : 31));
            var length = (packet[0] & 3) + 1;
            ulong truncated = 0;

            for (var i = 0; i < length; i++)
            {
                packet[numberOffset + i] ^= mask[i + 1];
                truncated = (truncated << 8) | packet[numberOffset + i];
            }

            number = Reconstruct(truncated, length, expected);
            var headerLength = numberOffset + length;
            var plaintext = new byte[packet.Length - headerLength - 16];
            Span<byte> nonce = stackalloc byte[12];
            Nonce(number, nonce);
            _aead.Decrypt(
                nonce,
                packet.AsSpan(headerLength, plaintext.Length),
                packet.AsSpan(packet.Length - 16),
                plaintext,
                packet.AsSpan(0, headerLength)
            );

            if ((packet[0] & (longHeader ? 12 : 24)) != 0 || (!longHeader && (packet[0] & 4) != 0))
                throw new InvalidDataException("Reserved bits or key updates are unsupported in this probe.");

            return plaintext;
        }

        internal static ulong Reconstruct(ulong truncated, int length, ulong expected)
        {
            var window = 1UL << (length * 8);
            var half = window / 2;
            var candidate = (expected & ~(window - 1)) | truncated;

            if (candidate + half <= expected && candidate < (1UL << 62) - window) return candidate + window;

            if (candidate > expected + half && candidate >= window) return candidate - window;

            return candidate;
        }

        private void Mask(byte[] packet, int offset, int length)
        {
            Span<byte> mask = stackalloc byte[16];
            _header.EncryptEcb(packet.AsSpan(offset + 4, 16), mask, PaddingMode.None);
            packet[0] ^= (byte)(mask[0] & ((packet[0] & 128) != 0 ? 15 : 31));

            for (var i = 0; i < length; i++) packet[offset + i] ^= mask[i + 1];
        }

        private void Nonce(ulong number, Span<byte> nonce)
        {
            _iv.CopyTo(nonce);
            Span<byte> encoded = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(encoded, number);

            for (var i = 0; i < 8; i++) nonce[i + 4] ^= encoded[i];
        }

        public void Dispose()
        {
            _aead.Dispose();
            _header.Dispose();
            CryptographicOperations.ZeroMemory(_iv);
        }
    }
}
