using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    internal static class ConnectionCoreTests
    {
        private static readonly byte[] Original = [1, 2, 3, 4, 5, 6, 7, 8];
        private static readonly byte[] Client = [9, 10, 11, 12, 13, 14, 15, 16];

        internal static void Run()
        {
            PeerConnectionIdTests.Run();
            LocalConnectionIdTests.Run();
            PathControlTests.Run();
            PathValidationTests.Run();
            StatelessResetTests.Run();
            AuthenticationPrecedesTls();
            InvalidLimitsPrecedeTls();
            DisposedOwnerRejectsWork();
            ConfiguredStreamLimit();
            FailedCleanupReleasesSiblings();
            AuthenticationLimits();

            foreach (var input in new byte[][]
            {
                [],
                [1],
                [6, 0, 1, 255],
                [255],
                [8, 0],
                [0x1c, 0, 0, 0]
            }) Mutate(input);
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            data = data[..Math.Min(data.Length, 4096)];
            PeerConnectionIdTests.Mutate(data);
            LocalConnectionIdTests.Mutate(data);
            PathControlTests.Mutate(data);
            PathValidationTests.Mutate(data);
            StatelessResetTests.Mutate(data);
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            var tls = new ModelTls(data.Length != 0 && (data[0] & 1) != 0);
            using var connection = new QuicConnection(
                first,
                _ => tls,
                static (port, _) => new StreamEchoApplication(port)
            );
            connection.Receive(first, 0);
            var packet = tls.Established
                ? ApplicationPacket(connection.ServerId, data)
                : NativeFuzz.Initial(keys, Original, Client, 1, data.ToArray());

            if (data.Length != 0 && (data[0] & 4) != 0) packet[^1] ^= 1;

            connection.Receive(packet, 1);
            connection.Tick(100000);
            Require(
                connection.Closed && connection.SentBytes <= 3 * connection.ReceivedBytes,
                "mutation-retirement-and-amplification"
            );
            connection.Dispose();
            connection.Dispose();
            Require(tls.DisposeCalls == 1, "tls-owned-once");

            try
            {
                connection.Receive(first, 2);
                throw new InvalidOperationException("Disposed connection accepted input.");
            }

            catch (ObjectDisposedException) { }

            MutateOwners(data, first);
        }

        private static void MutateOwners(ReadOnlySpan<byte> data, byte[] first)
        {
            var created = new List<ModelTls>();
            using var owners = new QuicConnectionSet(
                (packet, now) => new QuicConnection(
                    packet,
                    _ =>
                    {
                        var tls = new ModelTls(false);
                        created.Add(tls);
                        return tls;
                    },
                    static (port, _) => new StreamEchoApplication(port),
                    now
                ),
                capacity: 2
            );
            long sent = 0;
            long received = 0;
            var now = 0.0;

            for (var index = 0; index < Math.Min(8, data.Length); index++)
            {
                now += data[index] * 4;
                var peer = new IPEndPoint(IPAddress.Loopback, 16000 + data[index] % 3);
                received += first.Length;
                var output = owners.Receive(first, peer, now);
                Require(output.All(packet => packet.Peer.Equals(peer)), "mutation-reply-owner");
                sent += output.Sum(packet => packet.Bytes.Length);
                sent += owners
                    .Tick(now)
                    .Sum(packet => packet.Bytes.Length);
                Require(
                    owners.Active <= 2 && owners.Accepted - owners.Retired == owners.Active && sent <= 3 * received,
                    "mutation-owner-bounds"
                );
            }

            owners.Tick(now + 100000);
            Require(
                owners.Active == 0 && owners.Accepted == owners.Retired && created.All(tls => tls.DisposeCalls == 1),
                "mutation-owner-release"
            );
        }

        private static void AuthenticationPrecedesTls()
        {
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            first[^1] ^= 1;
            var calls = 0;

            try
            {
                using var connection = new QuicConnection(
                    first,
                    _ =>
                    {
                        calls++;
                        return new ModelTls(false);
                    },
                    static (port, _) => new StreamEchoApplication(port)
                );
                throw new InvalidOperationException("An unauthenticated Initial created a connection.");
            }

            catch (CryptographicException) { }

            Require(calls == 0, "invalid-initial-does-not-create-tls");
        }

        private static void InvalidLimitsPrecedeTls()
        {
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);

            foreach (var limits in new QuicConnectionLimits[]
            {
                new() { InboundBidirectionalStreams = 0 },
                new() { InboundBidirectionalStreams = 1025 },
                new() { InboundUnidirectionalStreams = 0 },
                new() { InboundUnidirectionalStreams = 1025 },
                new() { IdleTimeoutMilliseconds = 0 }
            })
            {
                var calls = 0;

                try
                {
                    using var connection = new QuicConnection(
                        first,
                        _ =>
                        {
                            calls++;
                            return new ModelTls(false);
                        },
                        static (port, _) => new StreamEchoApplication(port),
                        limits: limits
                    );
                    throw new InvalidOperationException("Invalid connection limits were accepted.");
                }

                catch (ArgumentOutOfRangeException) { }

                Require(calls == 0, "invalid-limits-before-tls");
            }
        }

        private static void DisposedOwnerRejectsWork()
        {
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            var tls = new ModelTls(true);
            using var connection = new QuicConnection(
                first,
                _ => tls,
                static (port, _) => new StreamEchoApplication(port)
            );
            connection.Receive(first, 0);
            connection.Dispose();

            foreach (var operation in new Action[]
            {
                () => connection.Receive(first, 1),
                () => connection.Tick(1),
                () => connection.PumpApplications(1),
                () => connection.CloseProtocol(1),
                () => connection.CloseApplication(0, 1)
            })
            {
                try
                {
                    operation();
                    throw new InvalidOperationException("A disposed packet owner accepted work.");
                }

                catch (ObjectDisposedException) { }
            }

            connection.CancelApplications();
            connection.Dispose();
            Require(tls.DisposeCalls == 1, "disposed-work-preserves-tls-retirement");
        }

        private static void ConfiguredStreamLimit()
        {
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            byte[]? advertised = null;
            using var connection = new QuicConnection(
                first,
                parameters =>
                {
                    advertised = parameters;
                    return new ModelTls(true);
                },
                static (port, _) => new AsyncStreamApplication(port),
                limits: new QuicConnectionLimits
                {
                    InboundBidirectionalStreams = 128,
                    InboundUnidirectionalStreams = 16,
                    IdleTimeoutMilliseconds = 60000
                }
            );
            var reader = new WireReader(
                advertised ?? throw new InvalidOperationException("Missing advertised limits.")
            );
            var fields = new Dictionary<ulong, ulong>();

            while (reader.Remaining != 0)
            {
                var id = reader.Integer();
                var value = reader.Take(reader.Length(4096));

                if (id is 1 or 8 or 9)
                {
                    var integer = new WireReader(value);
                    fields.Add(id, integer.Integer());
                }
            }

            Require(fields[1] == 60000 && fields[8] == 128 && fields[9] == 16, "advertised-production-limits");
            connection.Receive(first, 0);

            for (var index = 0U; index < 128; index++)
                connection.Receive(
                    ApplicationPacket(connection.ServerId, Wire.Frame(0x0b, index * 4, 0), index),
                    index + 1
                );

            for (var index = 0U; index < 16; index++)
                connection.Receive(
                    ApplicationPacket(connection.ServerId, Wire.Frame(0x0b, index * 4 + 2, 0), index + 128),
                    index + 129
                );

            Require(
                connection.Phase == ConnectionPhase.Active
                    && connection.Application is AsyncStreamApplication { Retained: 144 },
                "configured-full-async-admission"
            );
            var output = connection.Receive(
                ApplicationPacket(connection.ServerId, Wire.Frame(0x0b, 128 * 4, 0), 144),
                145
            );
            Require(connection.Phase == ConnectionPhase.Closing && output.Count == 1, "configured-next-stream-refused");
            using var applicationKeys = new PacketKeys(new byte[32]);
            var close = new WireReader(applicationKeys.Unprotect(output[0], 1 + Client.Length, 0, out _));
            Require(close.Integer() == 0x1c && close.Integer() == 4, "configured-stream-limit-error");
        }

        private static void AuthenticationLimits()
        {
            foreach (var suite in Enum.GetValues<QuicCipherSuite>())
            {
                foreach (var invalidTag in new[] { true, false })
                {
                    using var keys = PacketKeys.Initial(Original, false);
                    var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
                    var tls = new ModelTls(true) { CipherSuite = suite };
                    using var connection = new QuicConnection(
                        first,
                        _ => tls,
                        static (port, _) => new StreamEchoApplication(port)
                    );
                    connection.Receive(first, 0);
                    var limit =
                        (tls.ReadKeys[2]
                            ?? throw new InvalidOperationException("Missing model packet keys.")).AuthenticationLimit;
                    var counter = typeof(QuicConnection).GetField(
                        "_failedAuthentications",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                    )
                        ?? throw new InvalidOperationException("Missing authentication counter fixture.");
                    counter.SetValue(connection, limit - (invalidTag ? 1UL : 0UL));
                    var invalid = ApplicationPacket(connection.ServerId, Wire.Frame(1), 1, suite);

                    if (invalidTag) invalid[^1] ^= 1;

                    var output = connection.Receive(invalid, 1);
                    Require(
                        connection.Phase == ConnectionPhase.Closing && output.Count == 1,
                        "cipher-authentication-limit"
                    );
                    using var peer = new PacketKeys(new byte[PacketKeys.SecretLength(suite)], cipherSuite: suite);
                    var close = new WireReader(peer.Unprotect(output[0], 1 + Client.Length, 1, out _));
                    Require(close.Integer() == 0x1c && close.Integer() == 0x0f, "cipher-authentication-error");
                    Require(
                        connection.Receive(ApplicationPacket(connection.ServerId, Wire.Frame(1), 2, suite), 2).Count
                            == 0,
                        "cipher-stop-after-authentication-limit"
                    );
                }
            }
        }

        private static void FailedCleanupReleasesSiblings()
        {
            var created = new List<ModelTls>();
            var owners = new QuicConnectionSet(
                (packet, now) => new QuicConnection(
                    packet,
                    _ =>
                    {
                        var tls = new ModelTls(false, created.Count == 0);
                        created.Add(tls);
                        return tls;
                    },
                    static (port, _) => new StreamEchoApplication(port),
                    now
                )
            );
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);

            for (var index = 0; index < 3; index++)
                owners.Receive(first, new IPEndPoint(IPAddress.Loopback, 15000 + index), 0);

            try
            {
                owners.Dispose();
                throw new InvalidOperationException("A TLS cleanup failure was hidden.");
            }

            catch (AggregateException error)
            {
                Require(error.InnerExceptions.Count == 1, "cleanup-error-retained");
            }

            Require(
                owners.Active == 0
                    && owners.Accepted == 3
                    && owners.Retired == 3
                    && created.All(tls => tls.DisposeCalls == 1),
                "failed-cleanup-releases-siblings"
            );
            owners.Dispose();
        }

        internal static byte[] ApplicationPacket(
            ReadOnlySpan<byte> destination,
            ReadOnlySpan<byte> bytes,
            uint number = 0,
            QuicCipherSuite cipherSuite = QuicCipherSuite.Aes128GcmSha256
        )
        {
            var header = new byte[1 + destination.Length + 4];
            header[0] = 0x43;
            destination.CopyTo(header.AsSpan(1));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1 + destination.Length), number);
            using var keys = new PacketKeys(new byte[PacketKeys.SecretLength(cipherSuite)], cipherSuite: cipherSuite);
            return keys.Protect(header, 1 + destination.Length, number, bytes);
        }

        internal static byte[] HandshakePacket(ReadOnlySpan<byte> destination, ReadOnlySpan<byte> client)
        {
            using var header = new MemoryStream();
            header.Write(
                [
                    0xe3,
                    0,
                    0,
                    0,
                    1,
                    (byte)destination.Length
                ]
            );
            header.Write(destination);
            header.WriteByte((byte)client.Length);
            header.Write(client);
            Wire.Integer(header, 21);
            var offset = (int)header.Length;
            header.Write(new byte[4]);
            using var keys = new PacketKeys(new byte[32]);
            return keys.Protect(header.ToArray(), offset, 0, Wire.Frame(1));
        }

        // Deterministic TLS substitute for packet ownership/flow tests. It supplies
        // public test keys only; native and independent-peer suites verify real TLS.
        internal sealed class ModelTls(bool establish, bool failDispose = false) : TlsSession
        {
            private readonly Reassembly[] _input =
            [
                new(65536),
                new(65536),
                new(65536)
            ];

            internal QuicCipherSuite CipherSuite { get; set; } = QuicCipherSuite.Aes128GcmSha256;
            internal bool AllowEstablishment { get; set; } = establish;
            internal int DisposeCalls { get; private set; }

            internal override void Add(int level, ulong offset, ReadOnlySpan<byte> bytes) =>
                _input[level].Add(offset, bytes);

            internal override void Pump()
            {
                if (!AllowEstablishment || Established) return;

                using var parameters = new MemoryStream();
                Wire.Parameter(parameters, 15, Client);
                Wire.Parameter(parameters, 4, 1048576);
                Wire.Parameter(parameters, 5, 65536);
                Wire.Parameter(parameters, 6, 65536);
                Wire.Parameter(parameters, 7, 65536);
                Wire.Parameter(parameters, 8, 16);
                Wire.Parameter(parameters, 9, 16);
                PeerParameters = parameters.ToArray();
                ReadKeys[2] = new PacketKeys(new byte[PacketKeys.SecretLength(CipherSuite)], cipherSuite: CipherSuite);
                WriteKeys[2] = new PacketKeys(new byte[PacketKeys.SecretLength(CipherSuite)], cipherSuite: CipherSuite);
                Established = true;
            }

            public override void Dispose()
            {
                DisposeCalls++;
                ReleaseKeys();

                if (failDispose) throw new InvalidOperationException("Controlled TLS cleanup failure.");
            }
        }

        private static void Require(bool condition, string name) =>
            QuicFuzzChecks.Require(condition, "connection-core-" + name);
    }
}
