using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbedIO.Quic
{
    internal static class CipherSuiteTests
    {
        internal static void Run()
        {
            Span<byte> mask = stackalloc byte[5];
            ChaChaHeaderMask.Write(
                Convert.FromHexString("25a282b9e82f06f21f488917a4fc8f1b73573685608597d0efcb076b0ab7a7a4"),
                Convert.FromHexString("5e5cd55c41f69080575d7999c25a5bfb"),
                mask
            );
            Require(Convert.ToHexString(mask) == "AEFEFE7D03", "RFC 9001 A.5 header mask");
            ChaChaHeaderMask.Write(
                Enumerable
                    .Range(0, 32)
                    .Select(value => (byte)value)
                    .ToArray(),
                Convert.FromHexString("01000000000000090000004a00000000"),
                mask
            );
            Require(Convert.ToHexString(mask) == "10F1E7E4D1", "RFC 8439 block function");
            using var chacha = new PacketKeys(
                Convert.FromHexString("9ac312a7f877468ebe69422748ad00a15443f18203a07d6060f688f30f21632b"),
                cipherSuite: QuicCipherSuite.ChaCha20Poly1305Sha256
            );
            var payload = chacha.Unprotect(
                Convert.FromHexString("4cfe4189655e5cd55c41f69080575d7999c25a5bfb"),
                1,
                654360564,
                out var number
            );
            Require(
                payload
                    .AsSpan()
                    .SequenceEqual([(byte)1])
                    && number == 654360564,
                "RFC 9001 A.5 complete packet"
            );

            foreach (var suite in Enum.GetValues<QuicCipherSuite>())
            {
                var secret = new byte[PacketKeys.SecretLength(suite)];
                using var keys = new PacketKeys(secret, encryptionLimit: 4, cipherSuite: suite);
                using var next = keys.Next();
                Require(
                    next.CipherSuite == suite
                        && keys.AuthenticationLimit
                            == (suite == QuicCipherSuite.ChaCha20Poly1305Sha256 ? 1UL << 36 : 1UL << 52),
                    "cipher policy"
                );

                for (var index = 0; index < 4; index++)
                    next.Protect(
                        [
                            0x43,
                            0,
                            0,
                            0,
                            (byte)index
                        ],
                        1,
                        (ulong)index,
                        [1]
                    );

                Require(!next.CanEncrypt, "key update retains configured encryption budget");

                try
                {
                    next.Protect([0x43, 0, 0, 0, 4], 1, 4, [1]);
                    throw new InvalidOperationException("Cipher exceeded its encryption budget.");
                }

                catch (InvalidOperationException error) when (error.Message == "Packet encryption limit reached.") { }

                try
                {
                    using var invalid = new PacketKeys(secret.AsSpan(1), cipherSuite: suite);
                    throw new InvalidOperationException("Cipher accepted a truncated traffic secret.");
                }

                catch (ArgumentException) { }
            }

            try
            {
                using var invalid = new PacketKeys(new byte[32], cipherSuite: (QuicCipherSuite)0x1304);
                throw new InvalidOperationException("Unsupported TLS cipher was accepted.");
            }

            catch (ArgumentOutOfRangeException) { }

            if (OperatingSystem.IsWindows()) NativeProfiles();
        }

        [SupportedOSPlatform("windows")]
        private static void NativeProfiles()
        {
            foreach (var suite in new[] { QuicCipherSuite.Aes128GcmSha256, QuicCipherSuite.Aes256GcmSha384 })
            {
                var secret = new byte[398 + PacketKeys.SecretLength(suite)];
                Encoding.Unicode
                    .GetBytes("AES")
                    .CopyTo(secret, 0);
                Encoding.Unicode
                    .GetBytes("ChainingModeGCM")
                    .CopyTo(secret, 128);
                Encoding.Unicode
                    .GetBytes(suite == QuicCipherSuite.Aes256GcmSha384 ? "SHA384" : "SHA256")
                    .CopyTo(secret, 256);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    secret.AsSpan(384),
                    suite == QuicCipherSuite.Aes256GcmSha384 ? (ushort)32 : (ushort)16
                );
                BinaryPrimitives.WriteUInt16LittleEndian(secret.AsSpan(386), 12);
                BinaryPrimitives.WriteUInt16LittleEndian(secret.AsSpan(396), (ushort)PacketKeys.SecretLength(suite));
                Require(SchannelTls.ParseSecretProfile(secret) == suite, "native profile");

                for (var length = 0; length < secret.Length; length++)
                {
                    try
                    {
                        SchannelTls.ParseSecretProfile(secret.AsSpan(0, length));
                        throw new InvalidOperationException("Native TLS accepted a truncated secret.");
                    }

                    catch (InvalidDataException) { }
                }

                foreach (var offset in new[] { 0, 128, 256, 384, 386, 396 })
                {
                    secret[offset] ^= 1;

                    try
                    {
                        SchannelTls.ParseSecretProfile(secret);
                        throw new InvalidOperationException("Native TLS accepted inconsistent secret metadata.");
                    }

                    catch (InvalidDataException) { }

                    secret[offset] ^= 1;
                }
            }
        }

        internal static void Oracle(string input, string output)
        {
            if (new FileInfo(input).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Oversized cipher vectors.");

            using var document = JsonDocument.Parse(File.ReadAllBytes(input));

            if (document.RootElement.GetArrayLength() > 1024)
                throw new InvalidDataException("Too many cipher vectors.");

            var results = new List<object>();

            foreach (var value in document.RootElement.EnumerateArray())
            {
                var suite = (QuicCipherSuite)value
                    .GetProperty("suite")
                    .GetUInt16();
                var secret = Convert.FromHexString(
                    value
                        .GetProperty("secret")
                        .GetString()
                        ?? throw new InvalidDataException("Missing cipher vector text.")
                );
                var header = Convert.FromHexString(
                    value
                        .GetProperty("header")
                        .GetString()
                        ?? throw new InvalidDataException("Missing cipher vector text.")
                );
                var data = Convert.FromHexString(
                    value
                        .GetProperty("payload")
                        .GetString()
                        ?? throw new InvalidDataException("Missing cipher vector text.")
                );
                var sample = Convert.FromHexString(
                    value
                        .GetProperty("sample")
                        .GetString()
                        ?? throw new InvalidDataException("Missing cipher vector text.")
                );
                var offset = value
                    .GetProperty("offset")
                    .GetInt32();
                var number = value
                    .GetProperty("number")
                    .GetUInt64();

                if (
                    header.Length > 128
                    || data.Length > 4096
                    || offset < 1
                    || offset + 4 != header.Length
                    || sample.Length != 16
                )
                    throw new InvalidDataException("Invalid cipher vector boundary.");

                using var keys = new PacketKeys(secret, cipherSuite: suite);
                var packet = keys.Protect(header, offset, number, data);
                Require(
                    keys
                        .Unprotect(packet, offset, number, out var actual)
                        .AsSpan()
                        .SequenceEqual(data)
                        && actual == number,
                    "oracle roundtrip"
                );
                using var next = keys.Next();
                header[0] ^= 4;
                var updated = next.Protect(header, offset, number, data);
                Require(
                    next
                        .Unprotect(updated, offset, number, out actual, true)
                        .AsSpan()
                        .SequenceEqual(data)
                        && actual == number
                        && keys.Phase(updated, offset),
                    "oracle key update"
                );
                var mask = new byte[5];

                if (suite == QuicCipherSuite.ChaCha20Poly1305Sha256)
                    ChaChaHeaderMask.Write(PacketKeys.Expand(secret, "quic hp", 32, suite), sample, mask);

                results.Add(new
                {
                    packet = Convert.ToHexString(packet),
                    updated = Convert.ToHexString(updated),
                    mask = Convert.ToHexString(mask)
                });
            }

            File.WriteAllText(output, JsonSerializer.Serialize(results));
        }

        private static void Require(bool condition, string invariant)
        {
            if (!condition) throw new InvalidOperationException("Cipher invariant: " + invariant);
        }
    }
}
