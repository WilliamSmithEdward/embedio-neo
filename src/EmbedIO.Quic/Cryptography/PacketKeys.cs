using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EmbedIO.Quic
{
    // Original implementation of RFC 9001 sections 5.1-5.4; payload AEADs are BCL implementations.
    internal sealed class PacketKeys : IDisposable
    {
        private readonly AesGcm? _aead;
        private readonly ChaCha20Poly1305? _chacha;
        private readonly byte[] _headerKey = [];
        private readonly Aes? _aesHeader;
        private readonly byte[] _iv;
        private readonly byte[] _secret;
        private bool _disposed;
        internal const int MaximumEncryptions = 1 << 23;
        private readonly int _encryptionLimit;

        internal QuicCipherSuite CipherSuite { get; }
        internal ulong AuthenticationLimit =>
            CipherSuite == QuicCipherSuite.ChaCha20Poly1305Sha256 ? 1UL << 36 : 1UL << 52;
        internal int Encryptions { get; private set; }
        internal bool CanEncrypt => Encryptions < _encryptionLimit;

        internal PacketKeys(
            ReadOnlySpan<byte> secret,
            byte[]? retainedHeaderKey = null,
            int encryptionLimit = MaximumEncryptions,
            QuicCipherSuite cipherSuite = QuicCipherSuite.Aes128GcmSha256
        )
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(encryptionLimit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(encryptionLimit, MaximumEncryptions);
            var secretLength = SecretLength(cipherSuite);

            if (secret.Length != secretLength)
                throw new ArgumentException("Invalid TLS traffic-secret length.", nameof(secret));

            CipherSuite = cipherSuite;
            _encryptionLimit = encryptionLimit;
            _secret = secret.ToArray();
            var keyLength = cipherSuite == QuicCipherSuite.Aes128GcmSha256 ? 16 : 32;
            var key = Array.Empty<byte>();
            var headerKey = retainedHeaderKey ?? [];
            _iv = [];

            try
            {
                key = Expand(secret, "quic key", keyLength, cipherSuite);
                headerKey = retainedHeaderKey ?? Expand(secret, "quic hp", keyLength, cipherSuite);
                _iv = Expand(secret, "quic iv", 12, cipherSuite);

                if (headerKey.Length != keyLength)
                    throw new ArgumentException("Invalid header-protection key length.", nameof(retainedHeaderKey));

                if (cipherSuite == QuicCipherSuite.ChaCha20Poly1305Sha256)
                {
                    _chacha = new ChaCha20Poly1305(key);
                    _headerKey = [.. headerKey];
                }

                else
                {
                    _aead = new AesGcm(key, 16);
                    _aesHeader = Aes.Create();
                    _aesHeader.Key = headerKey;
                }
            }

            catch
            {
                _aead?.Dispose();
                _chacha?.Dispose();
                _aesHeader?.Dispose();
                CryptographicOperations.ZeroMemory(_iv);
                CryptographicOperations.ZeroMemory(_secret);
                throw;
            }

            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(headerKey);
            }
        }

        internal static int SecretLength(QuicCipherSuite cipherSuite) => cipherSuite switch
        {
            QuicCipherSuite.Aes128GcmSha256 or QuicCipherSuite.ChaCha20Poly1305Sha256 => 32,
            QuicCipherSuite.Aes256GcmSha384 => 48,
            _ => throw new ArgumentOutOfRangeException(nameof(cipherSuite))
        };

        internal static byte[] Expand(
            ReadOnlySpan<byte> secret,
            string label,
            int count,
            QuicCipherSuite cipherSuite = QuicCipherSuite.Aes128GcmSha256
        )
        {
            var size = SecretLength(cipherSuite);
            var name = Encoding.ASCII.GetBytes("tls13 " + label);
            var info = new byte[4 + name.Length];
            BinaryPrimitives.WriteUInt16BigEndian(info, checked((ushort)count));
            info[2] = checked((byte)name.Length);
            name.CopyTo(info, 3);
            var output = new byte[count];
            HKDF.Expand(size == 48 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256, secret, output, info);
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
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!CanEncrypt || number > PacketNumberWindow.Maximum)
                throw new InvalidOperationException("Packet encryption limit reached.");

            Encryptions++;
            var packet = new byte[header.Length + plaintext.Length + 16];
            header.CopyTo(packet, 0);
            Span<byte> nonce = stackalloc byte[12];
            Nonce(number, nonce);

            if (_aead != null)
                _aead.Encrypt(
                    nonce,
                    plaintext,
                    packet.AsSpan(header.Length, plaintext.Length),
                    packet.AsSpan(packet.Length - 16),
                    header
                );

            else if (_chacha != null)
                _chacha.Encrypt(
                    nonce,
                    plaintext,
                    packet.AsSpan(header.Length, plaintext.Length),
                    packet.AsSpan(packet.Length - 16),
                    header
                );

            else throw new InvalidOperationException("Packet AEAD is unavailable.");

            Mask(packet, numberOffset, 4);
            return packet;
        }

        internal readonly record struct ProtectedPacket(byte[] Bytes, int HeaderLength, ulong Number)
        {
            internal bool LongHeader => (Bytes[0] & 128) != 0;
            internal bool Phase => (Bytes[0] & 4) != 0;
        }

        internal ProtectedPacket Decode(ReadOnlySpan<byte> protectedPacket, int numberOffset, ulong expected)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (numberOffset < 0 || numberOffset + 20 > protectedPacket.Length)
                throw new InvalidDataException("Short protected packet.");

            var packet = protectedPacket.ToArray();
            Span<byte> mask = stackalloc byte[16];

            if (_aesHeader is Aes _header)
                _header.EncryptEcb(packet.AsSpan(numberOffset + 4, 16), mask, PaddingMode.None);

            else
                ChaChaHeaderMask.Write(_headerKey, packet.AsSpan(numberOffset + 4, 16), mask);

            var longHeader = (packet[0] & 128) != 0;
            packet[0] ^= (byte)(mask[0] & (longHeader ? 15 : 31));
            var length = (packet[0] & 3) + 1;
            ulong truncated = 0;

            for (var i = 0; i < length; i++)
            {
                packet[numberOffset + i] ^= mask[i + 1];
                truncated = (truncated << 8) | packet[numberOffset + i];
            }

            return new ProtectedPacket(packet, numberOffset + length, Reconstruct(truncated, length, expected));
        }

        internal byte[] Unprotect(
            ReadOnlySpan<byte> protectedPacket,
            int numberOffset,
            ulong expected,
            out ulong number,
            bool phase = false
        )
        {
            var packet = Decode(protectedPacket, numberOffset, expected);
            number = packet.Number;
            return Unprotect(packet, phase);
        }

        internal byte[] Unprotect(ProtectedPacket packet, bool phase)
        {
            var plaintext = new byte[packet.Bytes.Length - packet.HeaderLength - 16];
            Span<byte> nonce = stackalloc byte[12];
            Nonce(packet.Number, nonce);

            if (_aead != null)
                _aead.Decrypt(
                    nonce,
                    packet.Bytes.AsSpan(packet.HeaderLength, plaintext.Length),
                    packet.Bytes.AsSpan(packet.Bytes.Length - 16),
                    plaintext,
                    packet.Bytes.AsSpan(0, packet.HeaderLength)
                );

            else if (_chacha != null)
                _chacha.Decrypt(
                    nonce,
                    packet.Bytes.AsSpan(packet.HeaderLength, plaintext.Length),
                    packet.Bytes.AsSpan(packet.Bytes.Length - 16),
                    plaintext,
                    packet.Bytes.AsSpan(0, packet.HeaderLength)
                );

            else throw new InvalidOperationException("Packet AEAD is unavailable.");

            if (
                (packet.Bytes[0] & (packet.LongHeader ? 12 : 24)) != 0
                || (!packet.LongHeader && packet.Phase != phase)
                || packet.Number > PacketNumberWindow.Maximum
            )
                throw new InvalidDataException("Invalid reserved bits, key phase or packet number.");

            return plaintext;
        }

        internal PacketKeys Next()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var secret = Expand(_secret, "quic ku", SecretLength(CipherSuite), CipherSuite);

            try
            {
                return new PacketKeys(secret, _aesHeader?.Key ?? [.. _headerKey], _encryptionLimit, CipherSuite);
            }

            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        internal bool Phase(ReadOnlySpan<byte> packet, int offset)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Span<byte> mask = stackalloc byte[16];

            if (_aesHeader is Aes _header)
                _header.EncryptEcb(packet.Slice(offset + 4, 16), mask, PaddingMode.None);

            else
                ChaChaHeaderMask.Write(_headerKey, packet.Slice(offset + 4, 16), mask);

            return ((packet[0] ^ mask[0]) & 4) != 0;
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

            if (_aesHeader is Aes _header)
                _header.EncryptEcb(packet.AsSpan(offset + 4, 16), mask, PaddingMode.None);

            else
                ChaChaHeaderMask.Write(_headerKey, packet.AsSpan(offset + 4, 16), mask);

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
            if (_disposed) return;

            _disposed = true;
            _aead?.Dispose();
            _chacha?.Dispose();
            _aesHeader?.Dispose();

            CryptographicOperations.ZeroMemory(_headerKey);

            CryptographicOperations.ZeroMemory(_iv);
            CryptographicOperations.ZeroMemory(_secret);
        }
    }
}
