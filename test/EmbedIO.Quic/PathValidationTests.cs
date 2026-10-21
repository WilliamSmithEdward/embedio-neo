using System.Net;

namespace EmbedIO.Quic
{
    internal static class PathValidationTests
    {
        private static readonly byte[] Id = [1, 2, 3, 4];

        private static QuicConnectionEndpoints Endpoint(int port, int local = 19000) =>
            new(new IPEndPoint(IPAddress.Loopback, port), new IPEndPoint(IPAddress.Loopback, local));

        private static QuicPathValidation Create()
        {
            var paths = new QuicPathValidation(Endpoint(18000));
            var initial = paths.Receive(Endpoint(18000), Id, 1200, false) ?? throw new QuicFuzzFailure("initial path");
            paths.ValidateInitial();
            paths.Observe(initial, 0, true, 0, 1024);
            return paths;
        }

        private static QuicPath Move(QuicPathValidation paths, ulong number = 1, int port = 18001, int bytes = 40)
        {
            var path = paths.Receive(Endpoint(port), Id, bytes, true) ?? throw new QuicFuzzFailure("candidate path");
            paths.Observe(path, number, true, number, 1024);
            return path;
        }

        private static Delivery Probe(QuicPathValidation paths, QuicPath path, double now) =>
            paths
                .Probes(now)
                .Single(value => value.Path?.Id == path.Route.Id);

        internal static void Run()
        {
            Admission();
            Ordering();
            Validation();
            Retry();
            CooldownOrdering();
            Churn();
            PeerIds();
            PathRetirement();
            PeerRetirement();
            Reservations();
            EmptyPeerPool();
            EngineEmptyPeerPool();
            ClosingPath();
            LocalRetirementLimit();
            RecoveryEpoch();
            Engine();

            foreach (var data in new byte[][]
            {
                [],
                [0],
                [1, 2, 3, 4, 5, 6, 255]
            }) Mutate(data);

            Mutate(
                [
                    56, 152, 25, 4, 146, 107, 87, 58, 99, 108, 66, 40, 221, 255, 195, 127, 13, 29, 9, 254, 35, 42, 100,
                    215, 231, 106, 79, 8, 89, 197, 171, 199, 94, 105, 22, 250, 28, 93
                ]
            );

            var report = new
            {
                pathValidation = "passed",
                groups = 16
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
        }

        private static void Admission()
        {
            var paths = Create();
            var snapshot = paths.Snapshot;
            Check(
                paths.Receive(Endpoint(18001), Id, 1200, false) == null && paths.Count == 1,
                "no pre-confirmation migration"
            );
            Check(
                paths.Receive(Endpoint(18001, 19001), Id, 1200, true) == null && paths.Count == 1,
                "no local-address change"
            );
            paths.Receive(Endpoint(18000), Id, 50, true);
            Check(ReferenceEquals(snapshot, paths.Snapshot), "ordinary traffic reuses path publication");
            var path = Move(paths);
            Check(path.Allowance == 120 && paths.Current == path, "candidate has its own three-times budget");
            var probe = Probe(paths, path, 1);
            Check(probe.Path == path.Route && probe.PadToMinimum && !probe.Reliable, "targeted one-shot padded probe");
            QuicPathValidation.Sent(path, probe, 120, 1);
            Check(
                path.Allowance == 0 && paths.Find(0)?.Allowance == long.MaxValue,
                "new path cannot borrow old validation"
            );
        }

        private static void Ordering()
        {
            var paths = Create();
            var path = paths.Receive(Endpoint(18001), Id, 100, true) ?? throw new QuicFuzzFailure("probe path");
            Check(
                !paths.Observe(path, 100, false, 1, 1024) && paths.Current.Route.Id == 0,
                "probing packet does not steer"
            );
            Check(
                !paths.Observe(path, 99, true, 2, 1024) && paths.Current.Route.Id == 0,
                "reordered non-probing packet does not steer"
            );
            Check(paths.Observe(path, 101, true, 3, 1024), "highest non-probing packet steers");
            var probes = paths.Probes(3);
            Check(
                probes.Count == 2
                    && probes
                        .Select(value => Convert.ToHexString(value.Bytes.AsSpan(1)))
                        .Distinct()
                        .Count()
                        == 2,
                "old and new path get independent challenges"
            );
        }

        private static void Validation()
        {
            var paths = Create();
            var path = Move(paths);
            var probe = Probe(paths, path, 1);

            try
            {
                paths.Respond(probe.Bytes.AsSpan(1), 2);
                throw new QuicFuzzFailure("unsent nonce accepted");
            }

            catch (TransportException error) when (error.ErrorCode == 10) { }

            QuicPathValidation.Sent(path, probe, 120, 2);
            Check(
                paths.Respond(probe.Bytes.AsSpan(1), 3) == path && path.AddressValidated && !path.MtuValidated,
                "small proof validates address only"
            );
            var full = Probe(paths, path, 3);
            Check(
                !full.Bytes
                    .AsSpan(1)
                    .SequenceEqual(probe.Bytes.AsSpan(1)),
                "MTU phase uses a fresh nonce"
            );
            QuicPathValidation.Sent(path, full, 1200, 3);
            paths.Respond(full.Bytes.AsSpan(1), 4);
            Check(path.MtuValidated && !path.Validating, "full-size proof completes validation");
            Check(paths.Respond(probe.Bytes.AsSpan(1), 5) == null, "late completed proof is harmless");
            var failed = Move(paths, 6, 18002);
            var pending = Probe(paths, failed, 6);
            paths.Tick(4000);
            Check(
                paths.Current == path && pending.Acknowledged,
                "untransmitted candidate expires to the validated fallback"
            );
        }

        private static void Retry()
        {
            var paths = Create();
            var path = Move(paths, bytes: 400);
            var first = Probe(paths, path, 1);
            QuicPathValidation.Sent(path, first, 1200, 1);
            Check(paths.Probes(1024).Count == 0, "probe is not repeated before PTO");
            var second = Probe(paths, path, 1025);
            QuicPathValidation.Sent(path, second, 40, 1025);
            var third = Probe(paths, path, 2049);
            QuicPathValidation.Sent(path, third, 40, 2049);
            Check(paths.Probes(3072).Count == 0 && paths.Count == 2, "bounded attempts before expiry");
            paths.Tick(3073);
            Check(paths.Current.Route.Id == 0 && paths.Count == 1, "failed validation restores original path");
            Check(
                paths.Respond(first.Bytes.AsSpan(1), 3074) == null && paths.Count == 1,
                "late failed proof cannot resurrect a route"
            );
            Check(
                paths.Receive(Endpoint(18001), Id, 1200, true, 3074) == null,
                "failed-path cooldown prevents immediate route oscillation"
            );
            var retried = paths.Receive(Endpoint(18001), Id, 1200, true, 7000)
                ?? throw new QuicFuzzFailure("path retry after cooldown");
            Check(
                retried.Route.Id > path.Route.Id && !retried.AddressValidated && !retried.MtuValidated,
                "retry gets a fresh route and fresh proof"
            );
        }

        private static void CooldownOrdering()
        {
            var paths = Create();
            var first = paths.Receive(Endpoint(18001), Id, 40, true, 1) ?? throw new QuicFuzzFailure("long-PTO path");
            paths.Observe(first, 1, true, 1, 3000);
            paths.Tick(10000);
            var second = paths.Receive(Endpoint(18002), Id, 40, true, 10001)
                ?? throw new QuicFuzzFailure("short-PTO path");
            paths.Observe(second, 2, true, 10001, 1024);
            paths.Tick(14000);
            Check(
                paths.Receive(Endpoint(18002), Id, 40, true, 18000) != null,
                "later cooldown with an earlier expiry does not wait for queue head"
            );
            Check(paths.Receive(Endpoint(18001), Id, 40, true, 18000) == null, "longer cooldown is still enforced");
        }

        private static void Churn()
        {
            var paths = Create();

            for (ulong index = 1; index <= 500; index++)
            {
                var path = Move(paths, index, 18000 + (int)index);
                var probe = Probe(paths, path, index);
                QuicPathValidation.Sent(path, probe, 40, index);
                Check(
                    paths.Count <= QuicPathValidation.Capacity && paths.RetainedChallenges <= 33,
                    "path and nonce history stay bounded"
                );
            }

            paths.Tick(10000);
            Check(paths.Count == 1 && paths.Current.Route.Id == 0, "churn returns to the original fallback");
        }

        private static void PeerIds()
        {
            using var ids = new PeerConnectionIds(
                Id,
                static sequence => new Delivery(2, Wire.Frame(0x19, sequence)),
                true
            );
            ids.Add(0, 0, Id, new byte[16]);
            ids.Add(
                1,
                0,
                [5, 6, 7, 8],
                [
                    1,
                    .. new byte[15]
                ]
            );
            Check(
                ids.Resolve(null, out var sequence, out var id)
                    && sequence == 1
                    && id.Span.SequenceEqual(new byte[] { 5, 6, 7, 8 }),
                "new path selects unused peer CID"
            );
            ids.MarkUsed(sequence);
            var tokens = ids.ResetTokens;
            Check(
                tokens.Count == 2 && ReferenceEquals(tokens, ids.ResetTokens),
                "both used active tokens have stable publication"
            );

            foreach (var token in tokens)
                Check(
                    ids.MatchesReset(
                        [
                            0x40,
                            .. new byte[4],
                            .. token.ToArray()
                        ]
                    ),
                    "each used active token is eligible"
                );

            Check(!ids.Resolve(null, out _, out _), "cannot reuse a CID on an unrelated path");
            ids.Add(
                2,
                1,
                [9, 10, 11, 12],
                [
                    2,
                    .. new byte[15]
                ]
            );
            Check(
                ids.ResetTokens.Count == 1 && tokens.Count == 2 && !ids.MatchesReset(new byte[21]),
                "retirement removes only its token without mutating old snapshots"
            );
        }

        private static void PathRetirement()
        {
            var paths = Create();
            var initial = paths.Current;
            var snapshot = paths.Snapshot;
            var path = Move(paths, bytes: 400);
            var probes = paths.Probes(1);
            var oldProbe = probes.Single(value => value.Path?.Id == initial.Route.Id);
            var newProbe = probes.Single(value => value.Path?.Id == path.Route.Id);
            QuicPathValidation.Sent(initial, oldProbe, 1200, 1);
            QuicPathValidation.Sent(path, newProbe, 1200, 1);
            paths.Respond(newProbe.Bytes.AsSpan(1), 2);
            Check(
                paths.Count == 1 && paths.Fallback == path && paths.Find(initial.Route.Id) == null,
                "full proof releases obsolete routes after initiating old-path validation"
            );
            Check(
                snapshot.Count == 1 && snapshot[0].Id == initial.Route.Id,
                "retirement does not mutate published routes"
            );
            Check(
                paths.Respond(oldProbe.Bytes.AsSpan(1), 3) == null,
                "late response cannot revive an abandoned old probe"
            );
            var candidate = Move(paths, 4, 18002);
            paths.Tick(4000);
            Check(
                paths.Current == path && paths.Fallback == path && paths.Find(candidate.Route.Id) == null,
                "subsequent failed migration keeps the new validated fallback"
            );
        }

        private static void Reservations()
        {
            using var ids = new PeerConnectionIds(
                Id,
                static sequence => new Delivery(2, Wire.Frame(0x19, sequence)),
                true
            );
            ids.Add(0, 0, Id, new byte[16]);
            ids.Add(
                1,
                0,
                [9],
                [
                    1,
                    .. new byte[15]
                ]
            );
            Check(ids.Resolve(null, out var sequence, out _) && sequence == 1, "unsent path reserves an unused ID");
            Check(!ids.Resolve(null, out _, out _), "another unsent unrelated path cannot reserve the same ID");
            Check(ids.ResetTokens.Count == 1, "reserving a destination does not make its reset token eligible");
            ids.MarkUsed(sequence);
            Check(ids.ResetTokens.Count == 2, "transmitting the reserved destination activates its token");
        }

        private static void PeerRetirement()
        {
            var retired = new List<(ulong Sequence, Delivery Delivery)>();
            using var ids = new PeerConnectionIds(
                Id,
                sequence =>
                {
                    var delivery = new Delivery(2, Wire.Frame(0x19, sequence));
                    retired.Add((sequence, delivery));
                    return delivery;
                },
                true
            );
            ids.Add(0, 0, Id, new byte[16]);
            ids.Add(
                2,
                0,
                [2],
                [
                    2,
                    .. new byte[15]
                ]
            );
            ids.MarkUsed(2);
            var snapshot = ids.ResetTokens;
            ids.RetireUnused([0]);
            Check(
                ids.IsActive(0)
                    && !ids.IsActive(2)
                    && ids.ActiveCount == 1
                    && ids.ResetTokens.Count == 1
                    && snapshot.Count == 2,
                "local retirement preserves lower active ID and immutable token publication"
            );
            ids.Add(
                2,
                0,
                [2],
                [
                    2,
                    .. new byte[15]
                ]
            );
            Check(retired.Count == 1, "pending retirement deduplicates delayed frames");
            retired[0].Delivery.Acknowledged = true;
            ids.Prune();
            ids.Add(
                1,
                0,
                [1],
                [
                    1,
                    .. new byte[15]
                ]
            );
            Check(
                !ids.IsActive(1) && ids.ActiveCount == 1 && retired[^1].Sequence == 1,
                "unseen lower sequence cannot reopen a locally retired range"
            );

            for (ulong sequence = 3; sequence <= 1000; sequence++)
            {
                foreach (var value in retired) value.Delivery.Acknowledged = true;

                retired.Clear();
                ids.Prune();
                ids.Add(
                    sequence,
                    0,
                    BitConverter.GetBytes(sequence),
                    [
                        3,
                        .. BitConverter.GetBytes(sequence),
                        .. new byte[7]
                    ]
                );
                Check(
                    ids.Resolve(null, out var selected, out _) && selected == sequence,
                    "replenishment supplies a fresh destination"
                );
                ids.MarkUsed(sequence);
                ids.RetireUnused([0]);
                Check(
                    ids.IsActive(0) && ids.ActiveCount == 1 && ids.RetainedRetirements == 1,
                    "higher-ID retirement retains bounded state across a long-lived lower path"
                );
            }
        }

        private static void EmptyPeerPool()
        {
            using var ids = new PeerConnectionIds(
                [0],
                static sequence => new Delivery(2, Wire.Frame(0x19, sequence)),
                true
            );
            ids.Add(2, 0, [2], new byte[16]);
            ids.MarkUsed(2);
            ids.RetireUnused([0]);
            ids.Add(
                1,
                1,
                [1],
                [
                    1,
                    .. new byte[15]
                ]
            );
            Check(
                ids.ActiveCount == 0 && ids.Current.IsEmpty && ids.ResetToken.IsEmpty && ids.ResetTokens.Count == 0,
                "reordered replacement can temporarily leave no usable ID or token"
            );
            Check(
                !ids.Resolve(null, out _, out _) && !ids.MatchesReset(new byte[21]),
                "retired destinations cannot be reused while waiting for replenishment"
            );
            ids.Add(2, 0, [2], new byte[16]);
            Check(ids.ActiveCount == 0, "delayed retired ID cannot repopulate an empty pool");
            ids.Add(
                3,
                1,
                [3],
                [
                    3,
                    .. new byte[15]
                ]
            );
            Check(
                ids.Resolve(null, out var sequence, out _) && sequence == 3 && ids.ResetTokens.Count == 0,
                "fresh ID restores output without prematurely enabling its reset token"
            );
            ids.MarkUsed(sequence);
            Check(ids.ResetTokens.Count == 1, "replenished token becomes eligible after transmission");
        }

        private static void EngineEmptyPeerPool()
        {
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var keys = PacketKeys.Initial(original, false);
            var first = NativeFuzz.Initial(keys, original, client, 0, [1]);
            using var connection = new QuicConnection(
                first,
                static _ => new ConnectionCoreTests.ModelTls(true),
                static (port, _) => new StreamEchoApplication(port)
            );
            connection.BeginRouting(Endpoint(18000));
            connection.Receive(first, 0);
            connection.BeginRouting(Endpoint(18000));
            connection.Receive(ConnectionCoreTests.ApplicationPacket(connection.ServerId, Wire.Frame(1), 0), 100);

            static byte[] PeerFrame(ulong sequence, ulong prior, byte value) =>
            [
                .. Wire.Frame(0x18, sequence, prior),
                1,
                value,
                value,
                .. new byte[15]
            ];

            connection.BeginRouting(Endpoint(18000));
            connection.Receive(ConnectionCoreTests.ApplicationPacket(connection.ServerId, PeerFrame(2, 0, 2), 1), 101);
            var spare = connection.IssuedConnectionIds[1].ToArray();
            connection.BeginRouting(Endpoint(18001));
            connection.Receive(ConnectionCoreTests.ApplicationPacket(spare, Wire.Frame(1), 2), 200);
            connection.BeginRouting(null);
            connection.Tick(4000);
            Check(connection.ActivePaths?.Count == 1, "failed higher-ID path releases its route");
            connection.BeginRouting(Endpoint(18000));
            var output = connection.Receive(
                ConnectionCoreTests.ApplicationPacket(connection.ServerId, PeerFrame(1, 1, 1), 3),
                4001
            );
            Check(
                connection.Phase == ConnectionPhase.Active
                    && output.Count == 0
                    && connection.ResetToken.IsEmpty
                    && connection.ResetTokens.Count == 0,
                "engine waits safely when reordered IDs retire the last usable destination"
            );
            connection.BeginRouting(Endpoint(18000));
            output = connection.Receive(
                ConnectionCoreTests.ApplicationPacket(connection.ServerId, PeerFrame(3, 1, 3), 4),
                4002
            );
            Check(
                connection.Phase == ConnectionPhase.Active
                    && output.Count != 0
                    && output.All(static bytes => bytes[0] < 128 && bytes[1] == 3),
                "engine resumes queued control traffic using only the fresh destination"
            );
        }

        private static void ClosingPath()
        {
            ClosePath(true, 1);
            ClosePath(false, 1161);
        }

        private static void ClosePath(bool smallCandidate, int payloadLength)
        {
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var keys = PacketKeys.Initial(original, false);
            var first = NativeFuzz.Initial(keys, original, client, 0, [1]);
            using var connection = new QuicConnection(
                first,
                static _ => new ConnectionCoreTests.ModelTls(true),
                static (port, _) => new StreamEchoApplication(port)
            );
            connection.BeginRouting(Endpoint(18000));
            connection.Receive(first, 0);
            connection.BeginRouting(Endpoint(18000));
            connection.Receive(ConnectionCoreTests.ApplicationPacket(connection.ServerId, Wire.Frame(1), 0), 100);
            Check(connection.IssuedConnectionIds.Count > 1, "spare local ID is available for path close case");

            if (smallCandidate)
            {
                byte[] spareId = [55];
                byte[] token =
                [
                    55,
                    .. new byte[15]
                ];
                byte[] frame =
                [
                    .. Wire.Frame(0x18, 1, 0),
                    (byte)spareId.Length,
                    .. spareId,
                    .. token
                ];
                connection.BeginRouting(Endpoint(18000));
                connection.Receive(ConnectionCoreTests.ApplicationPacket(connection.ServerId, frame, 1), 101);
            }

            var spare = connection.IssuedConnectionIds[1].ToArray();
            var payload = new byte[payloadLength];
            payload[0] = 1;
            connection.BeginRouting(Endpoint(18001));
            connection.Receive(ConnectionCoreTests.ApplicationPacket(spare, payload, smallCandidate ? 2U : 1U), 200);
            Check(connection.ActivePaths?.Count == 2, "new path is admitted but unvalidated");
            connection.BeginRouting(null);
            var output = connection.CloseApplication(42, 201);
            var routes = connection.OutputPaths;
            Check(
                connection.Phase == ConnectionPhase.Closing
                    && connection.ClosePackets == 1
                    && output.Count == 1
                    && routes?.Count
                    == 1
                    && routes[0]?.Endpoints.Peer.Port
                    == 18000
                    && output[0]
                        .AsSpan(1, client.Length)
                        .SequenceEqual(client)
                    && Plain(output[0], client)[0] == 0x1d,
                smallCandidate
                    ? "close uses fallback when current amplification is exhausted"
                    : "close uses fallback when current path lacks a peer ID"
            );
        }

        private static void LocalRetirementLimit()
        {
            using var ids = new PeerConnectionIds(
                [0],
                static sequence => new Delivery(2, Wire.Frame(0x19, sequence)),
                true
            );

            for (ulong sequence = 1; sequence <= PeerConnectionIds.RetirementLimit + 1; sequence++)
            {
                ids.Add(
                    sequence,
                    0,
                    BitConverter.GetBytes(sequence),
                    [
                        .. BitConverter.GetBytes(sequence),
                        .. new byte[8]
                    ]
                );
                ids.MarkUsed(sequence);

                try
                {
                    ids.RetireUnused([0]);
                    Check(
                        sequence <= PeerConnectionIds.RetirementLimit,
                        "unacknowledged local retirement must stop at its bound"
                    );
                }

                catch (TransportException error) when (error.ErrorCode == 9)
                {
                    Check(
                        sequence == PeerConnectionIds.RetirementLimit + 1
                            && ids.IsActive(0)
                            && ids.ActiveCount == 1
                            && ids.RetainedRetirements == PeerConnectionIds.RetirementLimit,
                        "retirement limit preserves a destination for connection close"
                    );
                }
            }
        }

        private static void RecoveryEpoch()
        {
            var repeated = new List<Delivery>();
            var recovery = new Recovery((delivery, _) => repeated.Add(delivery)) { HandshakeConfirmed = true };
            recovery.SentPacket(2, 0, 1000, 0, new Delivery(2, [1]), false);
            recovery.Acknowledge(2, [(0, 0)], 0, 10);
            var old = new Delivery(2, [1]);
            recovery.SentPacket(2, 1, 1000, 11, old, false);
            recovery.ChangePath(20);
            recovery.SentPacket(2, 2, 1000, 21, new Delivery(2, [1]), false);
            recovery.Acknowledge(2, [(1, 1)], 0, 1000);
            Check(
                old.Acknowledged
                    && recovery.BytesInFlight == 1000
                    && recovery.CongestionWindow == 12000
                    && recovery.SmoothedRtt == 333
                    && recovery.AcknowledgmentCount == 0,
                "old-path ACK cannot train or subtract new flight"
            );
            recovery.Acknowledge(2, [(2, 2)], 0, 1021);
            Check(
                recovery.BytesInFlight == 0 && recovery.CongestionWindow == 13000 && recovery.SmoothedRtt == 1000,
                "new path trains from its own packets"
            );
            recovery = new Recovery((delivery, _) => repeated.Add(delivery));

            for (ulong number = 0; number < 4; number++)
                recovery.SentPacket(2, number, 1000, 0, new Delivery(2, [1]), false);

            recovery.ChangePath(1);
            recovery.SentPacket(2, 4, 1000, 2, new Delivery(2, [1]), false);
            recovery.Acknowledge(2, [(3, 3)], 0, 3);
            recovery.Tick(1000);
            Check(
                repeated.Count >= 3
                    && recovery.CongestionEvents == 0
                    && recovery.CongestionWindow == 12000
                    && recovery.BytesInFlight == 1000
                    && recovery.LossHistoryCount == 0,
                "old loss retries reliable data without reducing new congestion state"
            );
            recovery.Discard(2);
            Check(recovery.BytesInFlight == 0, "discard does not double-subtract old epochs");
        }

        private static void Engine()
        {
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var keys = PacketKeys.Initial(original, false);
            var first = NativeFuzz.Initial(keys, original, client, 0, [1]);
            using var connection = new QuicConnection(
                first,
                static _ => new ConnectionCoreTests.ModelTls(true),
                static (port, _) => new StreamEchoApplication(port)
            );
            connection.BeginRouting(Endpoint(18000));
            connection.Receive(first, 0);
            connection.BeginRouting(Endpoint(18000));
            connection.Receive(ConnectionCoreTests.ApplicationPacket(connection.ServerId, Wire.Frame(1), 0), 100);
            var packet = ConnectionCoreTests.ApplicationPacket(connection.ServerId, Wire.Frame(1), 1);
            var forged = packet.ToArray();
            forged[^1] ^= 1;
            connection.BeginRouting(Endpoint(18001));
            connection.Receive(forged, 101);
            Check(
                connection.ActivePaths?.Count == 1 && !connection.DatagramAuthenticated && connection.OutputPaths?.All(
                    static route => route?.Endpoints.Peer.Port == 18000
                )
                    == true,
                "failed authentication cannot allocate a path"
            );
            connection.BeginRouting(Endpoint(18001));
            var output = connection.Receive(packet, 102);
            var routes = connection.OutputPaths ?? throw new QuicFuzzFailure("missing packet routes");
            Check(
                connection.DatagramAuthenticated && connection.ActivePaths?.Count == 2 && routes.Count == output.Count,
                "authenticated migration publishes aligned routes"
            );
            var (Bytes, Route) = output
                .Select((bytes, index) => (Bytes: bytes, Route: routes[index]))
                .First(value => value.Route?.Endpoints.Peer.Port == 18001 && Plain(value.Bytes, client)[0] == 0x1a);
            var nonce = Plain(Bytes, client)
                .AsSpan(1, 8)
                .ToArray();
            connection.BeginRouting(Endpoint(18000));
            output = connection.Receive(
                ConnectionCoreTests.ApplicationPacket(connection.ServerId, [0x1b, .. nonce], 2),
                103
            );
            routes = connection.OutputPaths ?? throw new QuicFuzzFailure("missing proof routes");
            Check(
                output
                    .Select((bytes, index) => (Bytes: bytes, Route: routes[index]))
                    .Any(
                        value => value.Route?.Endpoints.Peer.Port == 18001
                            && value.Bytes.Length == 1200
                            && Plain(value.Bytes, client)[0] == 0x1a
                    ),
                "response on old path validates probed new address and initiates MTU proof"
            );
        }

        private static byte[] Plain(byte[] packet, byte[] client)
        {
            using var peer = new PacketKeys(new byte[32]);
            return peer.Unprotect(packet, 1 + client.Length, 0, out _);
        }

        internal static void Mutate(ReadOnlySpan<byte> input)
        {
            var paths = Create();
            ulong number = 0;

            foreach (var value in input[..Math.Min(input.Length, 128)])
            {
                number++;
                var now = number * 100;
                var path = paths.Receive(Endpoint(18000 + value % 16), Id, value + 1, true, now);

                if (path == null) continue;

                paths.Observe(path, number, (value & 1) != 0, now, 1024);

                foreach (var delivery in paths.Probes(now))
                {
                    var target = paths.Find(delivery.Path?.Id ?? ulong.MaxValue);

                    if (target == null)
                    {
                        Check(delivery.Acknowledged, "retired queued probe was invalidated");
                        continue;
                    }

                    var size = (int)Math.Min(1200, target.Allowance);

                    if (size < 40) continue;

                    QuicPathValidation.Sent(target, delivery, size, now);

                    if ((value & 2) != 0) paths.Respond(delivery.Bytes.AsSpan(1), now);
                }

                paths.Tick(now);
                Check(
                    paths.Count <= QuicPathValidation.Capacity && paths.RetainedChallenges <= 33,
                    "mutation state bounds"
                );
            }

            paths.Tick(100000);
            Check(paths.Count <= QuicPathValidation.Capacity, "mutation retirement bounds");

            if (input.Length >= 3 && (input[0] & 31) == 0)
            {
                var smallCandidate = (input[1] & 1) != 0;
                var payloadLength = smallCandidate ? 1 + input[2] : 400 + input[2];
                ClosePath(smallCandidate, payloadLength);
            }
        }

        private static void Check(bool condition, string name) =>
            QuicFuzzChecks.Require(condition, "path-validation-" + name);
    }
}
