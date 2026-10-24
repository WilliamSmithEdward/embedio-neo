using System;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace EmbedIO.Quic
{
    // Address-validation tokens for Retry (RFC 9000 8.1.2). A token is sealed with a key
    // this listener draws at startup and binds the client's IP address, the original
    // destination ID and the Retry's source ID, which the client must use as its next
    // destination. Tokens expire after ten seconds; a new listener accepts none of an old one's.
    internal sealed class RetryTokens : IDisposable
    {
        private const int NonceLength = 12;
        private const int TagLength = 16;
        internal const double LifetimeMilliseconds = 10000;
        private readonly AesGcm _aead;

        // Each nonce is this key's random prefix and a counter, so none repeats under the key
        // however many tokens it seals.
        private readonly uint _prefix = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        private long _counter;

        // AesGcm keeps its own copy of the key; the drawn bytes are cleared at once.
        internal RetryTokens()
        {
            var key = RandomNumberGenerator.GetBytes(32);
            _aead = new AesGcm(key, TagLength);
            CryptographicOperations.ZeroMemory(key);
        }

        // nonce | sealed(issued milliseconds, original ID, retry ID) | tag
        internal byte[] Issue(ReadOnlySpan<byte> originalId, ReadOnlySpan<byte> retryId, IPAddress client, double now)
        {
            var plaintext = new byte[8 + 1 + originalId.Length + 1 + retryId.Length];
            BinaryPrimitives.WriteDoubleBigEndian(plaintext, now);
            plaintext[8] = (byte)originalId.Length;
            originalId.CopyTo(plaintext.AsSpan(9));
            plaintext[9 + originalId.Length] = (byte)retryId.Length;
            retryId.CopyTo(plaintext.AsSpan(10 + originalId.Length));
            var token = new byte[NonceLength + plaintext.Length + TagLength];
            BinaryPrimitives.WriteUInt32BigEndian(token, _prefix);
            BinaryPrimitives.WriteInt64BigEndian(token.AsSpan(4), Interlocked.Increment(ref _counter));
            _aead.Encrypt(
                token.AsSpan(0, NonceLength),
                plaintext,
                token.AsSpan(NonceLength, plaintext.Length),
                token.AsSpan(NonceLength + plaintext.Length),
                Address(client)
            );
            return token;
        }

        // Unknown: the token is not one this key sealed for this address, such as one another
        // server issued. Invalid: it is this listener's, but expired or redeemed with the wrong
        // destination ID. Redeemed: the original destination ID it carries.
        internal enum Outcome
        {
            Unknown,
            Invalid,
            Redeemed
        }

        internal Outcome Redeem(
            ReadOnlySpan<byte> token,
            IPAddress client,
            ReadOnlySpan<byte> destination,
            double now,
            out byte[]? originalId
        )
        {
            originalId = null;

            if (token.Length < NonceLength + 10 + TagLength || token.Length > NonceLength + 50 + TagLength)
                return Outcome.Unknown;

            var plaintext = new byte[token.Length - NonceLength - TagLength];

            try
            {
                _aead.Decrypt(
                    token[..NonceLength],
                    token.Slice(NonceLength, plaintext.Length),
                    token[^TagLength..],
                    plaintext,
                    Address(client)
                );
            }

            catch (AuthenticationTagMismatchException)
            {
                return Outcome.Unknown;
            }

            var issued = BinaryPrimitives.ReadDoubleBigEndian(plaintext);
            var originalLength = plaintext[8];

            if (originalLength > 20 || 10 + originalLength > plaintext.Length) return Outcome.Invalid;

            var retryLength = plaintext[9 + originalLength];

            if (
                10 + originalLength + retryLength != plaintext.Length
                || !(now >= issued && now - issued <= LifetimeMilliseconds)
                || !destination.SequenceEqual(plaintext.AsSpan(10 + originalLength, retryLength))
            )
                return Outcome.Invalid;

            originalId = plaintext
                .AsSpan(9, originalLength)
                .ToArray();
            return Outcome.Redeemed;
        }

        private static byte[] Address(IPAddress client) =>
            (client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client).GetAddressBytes();

        public void Dispose() => _aead.Dispose();
    }
}
