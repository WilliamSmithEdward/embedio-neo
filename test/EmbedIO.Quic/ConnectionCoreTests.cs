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
            OutboundQueuePressure();
            AuthenticationLimits();
            ReliableResetParameters();
            StreamErrorCodes();
            FrameErrorCodes();
            InitialFrameErrorCodes();
            UdpPayloadParameter();

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

        private static void OutboundQueuePressure() => QueuePressure(1024, 0, true);

        // reset_stream_at is 0x1d from draft-ietf-quic-reliable-stream-reset-08 on and
        // 0x17f7586d2cb571 before, which WebTransport draft-14 and Safari 27 use. The server
        // offers both, either enables RESET_STREAM_AT, and a non-empty value of either is refused.
        private static void ReliableResetParameters()
        {
            const ulong Current = 0x1d, Legacy = 0x17f7586d2cb571;
            byte[]? local = null;

            using (Connect([], parameters => local = parameters).Connection) { }

            var offered = new Dictionary<ulong, int>();
            var reader = new WireReader(local ?? throw new InvalidOperationException("No local parameters."));

            while (reader.Remaining != 0)
            {
                var id = reader.Integer();
                offered[id] = reader.Take(reader.Length(4096)).Length;
            }

            Require(
                offered.GetValueOrDefault(Current, -1) == 0 && offered.GetValueOrDefault(Legacy, -1) == 0,
                "reliable-reset-offered-under-both-codepoints"
            );

            foreach (var (name, ids, expected) in new (string, ulong[], bool)[]
            {
                ("none", [], false),
                ("current", [Current], true),
                ("legacy", [Legacy], true),
                ("both", [Current, Legacy], true)
            })
            {
                using var parameters = new MemoryStream();

                foreach (var id in ids) Wire.Parameter(parameters, id, []);

                var (connection, features) = Connect(parameters.ToArray(), null);

                using (connection)
                    Require(
                        connection.Phase == ConnectionPhase.Active && features?.ReliableReset == expected,
                        "reliable-reset-peer-" + name
                    );
            }

            foreach (var id in new[] { Current, Legacy })
            {
                using var parameters = new MemoryStream();
                Wire.Parameter(parameters, id, [0]);
                var (connection, features) = Connect(parameters.ToArray(), null);

                using (connection)
                    Require(
                        connection.Phase != ConnectionPhase.Active && features == null,
                        "reliable-reset-value-refused-0x" + id.ToString("x")
                    );
            }
        }

        private static (QuicConnection Connection, QuicPeerFeatures? Features) Connect(
            byte[] extra,
            Action<byte[]>? local
        )
        {
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            QuicPeerFeatures? features = null;
            var connection = new QuicConnection(
                first,
                parameters =>
                {
                    local?.Invoke(parameters);
                    return new ModelTls(true) { ExtraParameters = extra };
                },
                (operations, peer) =>
                {
                    features = peer;
                    return new StreamEchoApplication(operations);
                }
            );
            connection.Receive(first, 0);
            return (connection, features);
        }

        internal static bool MutateQueue(ReadOnlySpan<byte> data)
        {
            if (data.Length < 2 || (data[0] & 15) != 0) return false;

            QueuePressure(600 + data[1] * 4, data[0], false);
            return true;
        }

        private static void QueuePressure(int count, byte seed, bool finish)
        {
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            using var connection = new QuicConnection(
                first,
                static _ => new ModelTls(true),
                static (operations, _) => new StreamEchoApplication(operations)
            );
            connection.Receive(first, 0);
            Require(connection.Established && connection.Phase == ConnectionPhase.Active, "queue-pressure-fixture");

            var deliveries = new List<Delivery>();
            var recovery = new Recovery(connection.QueueRepeat) { HandshakeConfirmed = true };

            for (var index = 0; index < count; index++)
            {
                var delivery = new Delivery(2, Wire.Frame(1));
                deliveries.Add(delivery);
                recovery.SentPacket(2, (ulong)index, 1000, 0, delivery, false);
            }

            var largest = (ulong)(count - 1);
            recovery.Acknowledge(2, [(largest, largest)], 0, 1);
            Require(connection.DeferredDeliveries > 0, "loss-burst-defers-repeats-without-closing");
            var probe = new Delivery(2, Wire.Frame(1));
            connection.QueueRepeat(probe, true);

            var lastLate = finish ? largest : (ulong)(count / 2 + seed % 4);
            recovery.Acknowledge(2, [(0UL, lastLate)], 0, 2);

            for (var turn = 1; turn <= 4; turn++) connection.PumpApplications(turn);

            Require(connection.Phase == ConnectionPhase.Active && probe.Transmissions > 0, "probe-stays-live");

            if (finish)
                Require(connection.DeferredDeliveries == 0, "deferred-repeats-retire");

            else
                Require(
                    connection.DeferredDeliveries <= Recovery.MaximumActivePackets + 512
                        && deliveries.All(item => item.Acknowledged || item.Queued || item.Transmissions >= 2),
                    "queue-pressure-retains-unacknowledged-deliveries"
                );
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

        // A peer's stream error closes the connection with the code RFC 9000 and
        // draft-ietf-quic-reliable-stream-reset name: FLOW_CONTROL_ERROR past the stream's credit,
        // FINAL_SIZE_ERROR for a changed final size and FRAME_ENCODING_ERROR for a Reliable Size
        // above the Final Size. The server offers reset_stream_at, so RESET_STREAM_AT is accepted
        // from a peer that did not offer it.
        private static void StreamErrorCodes()
        {
            using var offer = new MemoryStream();
            Wire.Parameter(offer, 0x1d, []);

            foreach (var (name, offered, payload, expected) in new (string, bool, byte[], ulong?)[]
            {
                ("reset-at-without-offer", false, Wire.Frame(0x24, 0, 9, 10, 0), null),
                (
                    "stream-past-credit",
                    true,
                    [
                        .. Wire.Frame(0x0e, 0, 65536, 1),
                        7
                    ],
                    0x03
                ),
                (
                    "stream-past-largest-offset",
                    true,
                    [
                        .. Wire.Frame(0x0e, 0, StreamReassembly.MaximumOffset, 1),
                        7
                    ],
                    0x03
                ),
                ("reset-past-credit", true, Wire.Frame(0x24, 0, 9, 65537, 0), 0x03),
                (
                    "changed-final-size",
                    true,
                    [
                        .. Wire.Frame(0x24, 0, 9, 100, 10),
                        .. Wire.Frame(0x24, 0, 9, 99, 10)
                    ],
                    0x06
                ),
                ("reliable-above-final", true, Wire.Frame(0x24, 0, 9, 10, 11), 0x07)
            })
            {
                using var keys = PacketKeys.Initial(Original, false);
                var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
                using var connection = new QuicConnection(
                    first,
                    _ => new ModelTls(true) { ExtraParameters = offered ? offer.ToArray() : [] },
                    static (port, _) => new AsyncStreamApplication(port)
                );
                connection.Receive(first, 0);
                var output = connection.Receive(ApplicationPacket(connection.ServerId, payload), 1);

                if (!expected.HasValue)
                {
                    Require(connection.Phase == ConnectionPhase.Active, "stream-error-" + name);
                    continue;
                }

                Require(
                    connection.Phase == ConnectionPhase.Closing && output.Count == 1,
                    "stream-error-closes-" + name
                );
                using var applicationKeys = new PacketKeys(new byte[32]);
                var close = new WireReader(applicationKeys.Unprotect(output[0], 1 + Client.Length, 0, out _));
                Require(close.Integer() == 0x1c && close.Integer() == expected, "stream-error-code-" + name);
            }
        }

        // A frame the server cannot accept closes the connection with the code RFC 9000 names
        // for it: FRAME_ENCODING_ERROR for an unknown or badly formatted frame (sections 12.4
        // and 19), PROTOCOL_VIOLATION for a frame a server must not receive and for a packet with
        // no frames (12.4), CRYPTO_BUFFER_EXCEEDED past the CRYPTO buffer (7.5 and 19.6), and
        // STREAM_STATE_ERROR or STREAM_LIMIT_ERROR for a stream a frame cannot name (4.6 and
        // 19.13). DATAGRAM frames up to the 1,200 bytes the server advertises are accepted and
        // larger ones refused (RFC 9221, section 3). Every case runs before any is checked.
        private static void FrameErrorCodes()
        {
            var failures = new List<string>();

            foreach (var (name, payload, expected) in new (string, byte[], string)[]
            {
                ("unknown-type", Wire.Frame(0x21), "0x7"),
                ("unknown-type-after-ping", Wire.Frame(0x01, 0x1f), "0x7"),
                ("unknown-extension-type", Wire.Frame(0xaf, 0, 0, 0, 0), "0x7"),
                ("new-token", Wire.Frame(0x07, 1, 5), "0xa"),
                ("handshake-done", Wire.Frame(0x1e), "0xa"),
                ("empty-packet", [], "0xa"),
                ("padding-only", [0, 0, 0], "active"),
                ("truncated-type", [0x40], "0x7"),
                ("truncated-stream", [0x0a, 0, 5, 0x61, 0x62], "0x7"),
                ("truncated-reset-stream", Wire.Frame(0x04, 0, 1), "0x7"),
                ("truncated-stop-sending", Wire.Frame(0x05, 0), "0x7"),
                ("truncated-max-data", Wire.Frame(0x10), "0x7"),
                ("truncated-crypto", [0x06, 0, 5, 1], "0x7"),
                ("truncated-connection-close", Wire.Frame(0x1c, 0), "0x7"),
                ("truncated-datagram", [0x31, 5, 1], "0x7"),
                ("max-streams-at-limit", Wire.Frame(0x12, 1UL << 60), "active"),
                ("max-streams-above-limit", Wire.Frame(0x12, (1UL << 60) + 1), "0x7"),
                ("max-uni-streams-above-limit", Wire.Frame(0x13, (1UL << 60) + 1), "0x7"),
                ("streams-blocked-at-limit", Wire.Frame(0x16, 1UL << 60), "active"),
                ("streams-blocked-above-limit", Wire.Frame(0x16, (1UL << 60) + 1), "0x7"),
                ("uni-streams-blocked-above-limit", Wire.Frame(0x17, (1UL << 60) + 1), "0x7"),
                ("stream-data-blocked-open", Wire.Frame(0x15, 0, 0), "active"),
                ("stream-data-blocked-send-only", Wire.Frame(0x15, 3, 0), "0x5"),
                ("stream-data-blocked-unopened-local", Wire.Frame(0x15, 1, 0), "0x5"),
                ("stream-data-blocked-past-limit", Wire.Frame(0x15, 4 * 4096, 0), "0x4"),
                ("stop-sending-past-limit", Wire.Frame(0x05, 4 * 4096, 0), "0x4"),
                (
                    "crypto-past-buffer",
                    [
                        .. Wire.Frame(0x06, 65535, 2),
                        1,
                        2
                    ],
                    "0xd"
                ),
                (
                    "crypto-past-largest-offset",
                    [
                        .. Wire.Frame(0x06, (1UL << 62) - 1, 1),
                        1
                    ],
                    "0xd"
                ),
                ("datagram-within-limit", Datagram(0x31, 1150), "active datagram-1150"),
                ("datagram-at-limit", Datagram(0x31, 1197), "active datagram-1197"),
                ("datagram-past-limit", Datagram(0x31, 1198), "0xa"),
                ("datagram-to-end-at-limit", Datagram(0x30, 1199), "active datagram-1199"),
                ("datagram-to-end-past-limit", Datagram(0x30, 1200), "0xa")
            })
            {
                var outcome = FrameOutcome(payload);

                if (outcome != expected) failures.Add(name + " gave " + outcome);
            }

            Require(failures.Count == 0, "frame-error-codes: " + string.Join("; ", failures));
        }

        private static byte[] Datagram(byte type, int length)
        {
            using var frame = new MemoryStream();
            Wire.Integer(frame, type);

            if (type == 0x31) Wire.Integer(frame, (ulong)length);

            frame.Write(new byte[length]);
            return frame.ToArray();
        }

        // Sends one 1-RTT packet to an established connection. The outcome is "active" or the
        // CONNECTION_CLOSE error code the connection answered with, followed by any calls that
        // reached the application.
        private static string FrameOutcome(byte[] payload)
        {
            using var keys = PacketKeys.Initial(Original, false);
            var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            var application = new RecordingApplication();
            using var connection = new QuicConnection(first, _ => new ModelTls(true), (_, _) => application);
            connection.Receive(first, 0);
            var output = connection.Receive(ApplicationPacket(connection.ServerId, payload), 1);
            var calls = application.Calls.Count == 0 ? "" : " " + string.Join(",", application.Calls);

            if (connection.Phase == ConnectionPhase.Active) return "active" + calls;

            if (connection.Phase != ConnectionPhase.Closing || output.Count != 1) return connection.Phase + calls;

            using var applicationKeys = new PacketKeys(new byte[32]);
            var close = new WireReader(applicationKeys.Unprotect(output[0], 1 + Client.Length, 0, out _));

            return close.Integer() == 0x1c ? "0x" + close.Integer().ToString("x") + calls : "no-close" + calls;
        }

        // An unknown frame type is a FRAME_ENCODING_ERROR in an Initial packet too, while a known
        // frame that Initial packets cannot carry stays a PROTOCOL_VIOLATION (section 12.4).
        private static void InitialFrameErrorCodes()
        {
            foreach (var (name, payload, expected) in new (string, byte[], ulong)[]
            {
                ("initial-unknown-type", Wire.Frame(0x21), 0x07),
                ("initial-max-data", Wire.Frame(0x10, 1), 0x0a)
            })
            {
                using var keys = PacketKeys.Initial(Original, false);
                var first = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
                using var connection = new QuicConnection(
                    first,
                    _ => new ModelTls(false),
                    static (port, _) => new AsyncStreamApplication(port)
                );
                connection.Receive(first, 0);
                var replies = connection.Receive(NativeFuzz.Initial(keys, Original, Client, 1, payload), 1);
                Require(connection.Phase == ConnectionPhase.Closing && replies.Count == 1, "closes-" + name);
                var close = new WireReader(NativeFuzz.UnprotectInitial(replies[0], Original));
                Require(close.Integer() == 0x1c && close.Integer() == expected, "code-" + name);
            }
        }

        // max_udp_payload_size values below 1,200 are invalid; RFC 9000 section 18.2 sets no upper
        // bound, so a value above the largest UDP payload is accepted.
        private static void UdpPayloadParameter()
        {
            foreach (var (value, accepted) in new (ulong, bool)[]
            {
                (1199, false),
                (1200, true),
                (65527, true),
                (65535, true),
                ((1UL << 62) - 1, true)
            })
            {
                using var parameters = new MemoryStream();
                Wire.Parameter(parameters, 3, value);
                var (connection, _) = Connect(parameters.ToArray(), null);

                using (connection)
                    Require(
                        (connection.Phase == ConnectionPhase.Active) == accepted,
                        "udp-payload-parameter-" + value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    );
            }
        }

        private sealed class RecordingApplication : StreamApplication
        {
            internal List<string> Calls { get; } = [];

            internal override void Open(ulong id) => Calls.Add("open-" + id);

            internal override void Stream(ulong id, byte[] bytes, bool fin) => Calls.Add("stream-" + id);

            internal override void Reset(ulong id, ulong error, ulong reliableSize) => Calls.Add("reset-" + id);

            internal override void Stop(ulong id, ulong error) => Calls.Add("stop-" + id);

            internal override void ReceiveDatagram(byte[] bytes) => Calls.Add("datagram-" + bytes.Length);

            internal override bool CanRetire(ulong id) => true;

            internal override void Retire(ulong id) { }

            public override void Dispose() { }
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

            // Transport parameters appended to the peer's usual set.
            internal byte[] ExtraParameters { get; init; } = [];

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
                parameters.Write(ExtraParameters);
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
