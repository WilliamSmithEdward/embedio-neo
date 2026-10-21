using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace EmbedIO.Quic
{
    internal static class NativeFuzz
    {
        internal static void Run(
            string library,
            string certificate,
            string key,
            string seedText,
            string countText,
            string output
        )
        {
            var seed = int.Parse(seedText, CultureInfo.InvariantCulture);
            var count = int.Parse(countText, CultureInfo.InvariantCulture);

            if (count is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(countText));

            if (Directory.Exists(output))
                throw new InvalidOperationException("Use a fresh native fuzz evidence directory.");

            Directory.CreateDirectory(output);
            using var provider = new DevelopmentTlsProvider(library, certificate, key);
            Func<byte[], string, TlsSession> tlsFactory = provider.Create;
            var random = new Random(seed);
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var keys = PacketKeys.Initial(original, false);
            var input = Array.Empty<byte>();
            var index = 0;
            var nativeStarts = 0;

            try
            {
                CheckEarlyTlsFailure(library, certificate, key, keys, original, client, tlsFactory);
                CheckCloseLevels(library, certificate, key, keys, original, client, tlsFactory);
                CheckReceiveHistory(library, certificate, key, keys, original, client, tlsFactory);
                CheckEncryptionBudget(library, certificate, key, keys, original, client, tlsFactory);
                CheckAuthenticationBudget(library, certificate, key, keys, original, client, tlsFactory);
                ConnectionSetTests.Run(library, certificate, key, tlsFactory);
                ConnectionSetTests.Mutate(library, certificate, key, seed, Math.Min(count, 10000), tlsFactory);

                for (; index < count; index++)
                {
                    var bytes = new byte[random.Next(0, 513)];
                    random.NextBytes(bytes);
                    var hello = index % 3 == 2
                        ? TlsHelloTests.NativeHello(bytes, DevelopmentOpenSslTls.ApplicationProtocol)
                        : bytes;
                    input = index % 3 == 1
                        ? bytes
                        :
                        [
                            .. Wire.Frame(6, 0, (ulong)hello.Length),
                            .. hello
                        ];
                    var first = Initial(keys, original, client, 0, [1]);
                    using var connection = new ProbeConnection(
                        first,
                        library,
                        certificate,
                        key,
                        tlsFactory: tlsFactory
                    );
                    connection.Receive(first, 0);
                    var packet = Initial(keys, original, client, 1, input);

                    if (index % 5 == 0)
                    {
                        packet[^1] ^= 1;
                        var rejected = connection.RejectedPackets;
                        connection.Receive(packet, 1);
                        QuicFuzzChecks.Require(
                            connection.RejectedPackets > rejected && connection.Phase == ConnectionPhase.Active,
                            "native-unauthenticated-isolation"
                        );
                    }

                    else connection.Receive(packet, 1);

                    if (connection.NativeTlsStarted) nativeStarts++;

                    connection.Tick(100000);
                    QuicFuzzChecks.Require(
                        connection.Closed && connection.SentBytes <= 3 * connection.ReceivedBytes,
                        "native-lifetime-amplification"
                    );
                }
            }

            catch (Exception)
            {
                File.WriteAllBytes(Path.Combine(output, "failure.bin"), input);
                var failure = new
                {
                    seed,
                    index
                };
                File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(failure));
                throw;
            }

            var report = JsonSerializer.Serialize(new
            {
                seed,
                cases = count,
                passed = true,
                sockets = 0,
                nativeStarts,
                connectionSetSteps = Math.Min(count, 10000),
                scope = "Fresh TLS owners, native handshake admission and authenticated Initial frame/CRYPTO mutations"
            });
            File.WriteAllText(Path.Combine(output, "receipt.json"), report);
            Console.WriteLine(report);
        }

        private static void CheckEarlyTlsFailure(
            string library,
            string certificate,
            string key,
            PacketKeys keys,
            byte[] original,
            byte[] client,
            Func<byte[], string, TlsSession> tlsFactory
        )
        {
            byte[] source =
            [
                .. Wire.Frame(15, (ulong)client.Length),
                .. client
            ];

            foreach (var parameters in new byte[][]
            {
                [255],
                [
                    .. source,
                    .. Wire.Frame(3, 2, 1199)
                ],
                [
                    .. source,
                    .. Wire.Frame(3, 4, 65528)
                ]
            })
            {
                var hello = TlsHelloTests.NativeHello(parameters, DevelopmentOpenSslTls.ApplicationProtocol);
                byte[] crypto =
                [
                    .. Wire.Frame(6, 0, (ulong)hello.Length),
                    .. hello
                ];
                var first = Initial(keys, original, client, 0, crypto);
                using var connection = new ProbeConnection(first, library, certificate, key, tlsFactory: tlsFactory);
                var packets = connection.Receive(first, 0);
                QuicFuzzChecks.Require(
                    connection.Phase == ConnectionPhase.Closing && packets.Count == 1,
                    "early-tls-parameter-failure-closes"
                );
                var reader = new WireReader(UnprotectInitial(packets[0], original));
                QuicFuzzChecks.Require(
                    reader.Integer() == 0x1c && reader.Integer() == 8,
                    "early-parameter-error-is-readable"
                );
            }
        }

        private static void CheckAuthenticationBudget(
            string library,
            string certificate,
            string key,
            PacketKeys keys,
            byte[] original,
            byte[] client,
            Func<byte[], string, TlsSession> tlsFactory
        )
        {
            var first = Initial(keys, original, client, 0, [1]);
            using var connection = new ProbeConnection(
                first,
                library,
                certificate,
                key,
                authenticationLimit: 2,
                tlsFactory: tlsFactory
            );
            connection.Receive(first, 0);
            var forged = Initial(keys, original, client, 1, [1]);
            forged[^1] ^= 1;
            connection.Receive(forged, 1);
            QuicFuzzChecks.Require(
                connection.RejectedPackets == 1 && connection.Phase == ConnectionPhase.Active,
                "authentication-budget-tolerates-first-failure"
            );
            var responses = connection.Receive(forged, 2);
            QuicFuzzChecks.Require(
                connection.RejectedPackets == 2 && connection.Phase == ConnectionPhase.Closing && responses.Count == 1,
                "authentication-budget-closes"
            );
            var reader = new WireReader(UnprotectInitial(responses[0], original));
            QuicFuzzChecks.Require(
                reader.Integer() == 0x1c && reader.Integer() == 0x0f,
                "authentication-limit-wire-error"
            );
            QuicFuzzChecks.Require(
                connection.Receive(Initial(keys, original, client, 2, [1]), 3).Count == 0
                    && connection.RejectedPackets == 2
                    && connection.ClosePackets == 1,
                "authentication-budget-stops-receiving"
            );
            connection.Tick(100000);
            QuicFuzzChecks.Require(connection.Closed, "authentication-budget-retired");
        }

        private static void CheckEncryptionBudget(
            string library,
            string certificate,
            string key,
            PacketKeys keys,
            byte[] original,
            byte[] client,
            Func<byte[], string, TlsSession> tlsFactory
        )
        {
            var first = Initial(keys, original, client, 0, [1]);
            using var connection = new ProbeConnection(
                first,
                library,
                certificate,
                key,
                encryptionLimit: 16,
                tlsFactory: tlsFactory
            );
            var responses = connection.Receive(first, 0);

            for (uint number = 1; number <= 8; number++)
                responses = connection.Receive(
                    Initial(
                        keys,
                        original,
                        client,
                        number,
                        [
                            .. Wire.Frame(2, number - 1, 0, 0, 0),
                            1
                        ]
                    ),
                    number
                );

            QuicFuzzChecks.Require(
                connection.Phase == ConnectionPhase.Closing
                    && connection.ClosePackets == 1
                    && connection.SentPackets == 9
                    && responses.Count == 1,
                "aead-budget-closes-with-reserve"
            );
            var reader = new WireReader(UnprotectInitial(responses[0], original));
            QuicFuzzChecks.Require(reader.Integer() == 0x1c && reader.Integer() == 0x0f, "aead-limit-wire-error");
            connection.Tick(100000);
            QuicFuzzChecks.Require(connection.Closed, "aead-budget-owner-retired");
        }

        private static void CheckReceiveHistory(
            string library,
            string certificate,
            string key,
            PacketKeys keys,
            byte[] original,
            byte[] client,
            Func<byte[], string, TlsSession> tlsFactory
        )
        {
            var first = Initial(keys, original, client, 0, [1]);
            using var connection = new ProbeConnection(first, library, certificate, key, tlsFactory: tlsFactory);
            connection.Receive(first, 0);

            for (uint number = 1; number <= 10000; number++)
                connection.Receive(Initial(keys, original, client, number, Wire.Frame(2, 0, 0, 0, 0)), 1);

            QuicFuzzChecks.Require(
                connection.Phase == ConnectionPhase.Active && connection.ClosePackets == 0,
                "sustained-receive-history"
            );
            var responses = connection.Receive(first, 2);
            QuicFuzzChecks.Require(
                responses.Count == 1 && connection.Phase == ConnectionPhase.Active,
                "retired-duplicate-acknowledgment"
            );
            var plaintext = UnprotectInitial(responses[0], original);
            var reader = new WireReader(plaintext);
            QuicFuzzChecks.Require(
                reader.Integer() == 2
                    && reader.Integer() == 10000
                    && reader.Integer() == 0
                    && reader.Integer() == 0
                    && reader.Integer() == 4095,
                "bounded-receive-ack-range"
            );
        }

        private static byte[] UnprotectInitial(byte[] packet, byte[] original)
        {
            var header = new WireReader(packet);
            QuicFuzzChecks.Require((header.Byte() & 240) == 192, "initial-response-level");
            header.Take(4);
            header.Take(header.Byte());
            header.Take(header.Byte());
            header.Take(header.Length(4096));
            var length = header.Length(packet.Length);
            var offset = packet.Length - header.Remaining;
            using var serverKeys = PacketKeys.Initial(original, true);
            return serverKeys.Unprotect(packet.AsSpan(0, offset + length), offset, 0, out _);
        }

        private static void CheckCloseLevels(
            string library,
            string certificate,
            string key,
            PacketKeys keys,
            byte[] original,
            byte[] client,
            Func<byte[], string, TlsSession> tlsFactory
        )
        {
            var first = Initial(keys, original, client, 0, [1]);
            using var connection = new ProbeConnection(first, library, certificate, key, tlsFactory: tlsFactory);
            connection.Receive(first, 0);
            var replies = connection.Receive(Initial(keys, original, client, 1, Wire.Frame(0x1d, 0, 0)), 1);
            QuicFuzzChecks.Require(
                connection.Phase == ConnectionPhase.Closing && replies.Count == 1,
                "initial-application-close-rejected"
            );
            var plaintext = UnprotectInitial(replies[0], original);
            var close = new WireReader(plaintext);
            QuicFuzzChecks.Require(close.Integer() == 0x1c && close.Integer() == 0x0a, "initial-close-protocol-error");
            connection.Receive(Initial(keys, original, client, 2, Wire.Frame(0x1d, 0, 0)), 2);
            QuicFuzzChecks.Require(connection.Phase == ConnectionPhase.Closing, "closing-application-close-level");
            connection.Receive(Initial(keys, original, client, 3, Wire.Frame(0x1c, 0, 0, 0)), 3);
            QuicFuzzChecks.Require(connection.Phase == ConnectionPhase.Draining, "closing-transport-close-accepted");
        }

        internal static byte[] Initial(
            PacketKeys keys,
            byte[] original,
            byte[] client,
            uint number,
            ReadOnlySpan<byte> payload
        )
        {
            using var header = new MemoryStream();
            header.Write([0xc3, 0, 0, 0, 1, 8]);
            header.Write(original);
            header.WriteByte(8);
            header.Write(client);
            header.WriteByte(0);
            Wire.Integer(header, 1174);
            var offset = (int)header.Length;
            Span<byte> encoded = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(encoded, number);
            header.Write(encoded);
            var padded = new byte[1154];
            payload.CopyTo(padded);
            return keys.Protect(header.ToArray(), offset, number, padded);
        }
    }
}
