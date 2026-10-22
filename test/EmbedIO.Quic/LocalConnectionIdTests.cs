using System.Net;

namespace EmbedIO.Quic
{
    internal static class LocalConnectionIdTests
    {
        internal static void Run()
        {
            TokensAndSizes();
            Retirement(10000);
            FramesAndLoss();
            Mutate([0, 1, 4, 7, 8, 15, 16, 31]);
        }

        private static void TokensAndSizes()
        {
            using var first = new QuicConnectionIdIssuer();
            using var other = new QuicConnectionIdIssuer();
            var id = first.Create();
            var token = first.Token(id);
            Check(id.Length == QuicConnectionIdIssuer.Length && token.Length == 16, "fixed ID and token sizes");
            Check(
                token.SequenceEqual(first.Token(id)) && !token.SequenceEqual(other.Token(id)),
                "listener-bound derivation"
            );
            var changed = id.ToArray();
            changed[^1] ^= 1;
            Check(!token.SequenceEqual(first.Token(changed)), "ID-bound derivation");

            foreach (var length in new[] { 0, 1, 20, 21, 36, 37, 38, 43, 44, 128, 65535, 65536 })
            {
                foreach (var header in new byte[] { 0, 0x40, 0x7f, 0x80, 0xc0, 0xff })
                {
                    var packet = new byte[length];

                    if (length > 0) packet[0] = header;

                    if (length >= 17) id.CopyTo(packet.AsSpan(1));

                    var reset = first.Reset(packet);
                    var expected = length is >= 37 and <= 65535 && (header & 0xc0) == 0x40;
                    Check((reset != null) == expected, "reset input eligibility");

                    if (reset == null) continue;

                    Check(
                        reset.Length >= 21 && reset.Length < length && reset.Length <= 43,
                        "non-amplifying reset bound"
                    );
                    Check(
                        (reset[0] & 0xc0) == 0x40
                            && reset
                                .AsSpan(reset.Length - 16)
                                .SequenceEqual(token),
                        "reset layout and token"
                    );

                    if (length <= 43) Check(reset.Length == length - 1, "short reset shrinks by one");
                }
            }

            var eligible = new byte[128];
            eligible[0] = 0x40;
            id.CopyTo(eligible.AsSpan(1));
            var a = first.Reset(eligible) ?? throw new QuicFuzzFailure("local-id-first-reset-missing");
            var b = first.Reset(eligible) ?? throw new QuicFuzzFailure("local-id-second-reset-missing");
            Check(
                !a
                    .AsSpan(0, a.Length - 16)
                    .SequenceEqual(b.AsSpan(0, b.Length - 16)),
                "fresh reset prefix"
            );
            first.Dispose();

            try
            {
                first.Token(id);
                throw new QuicFuzzFailure("issuer-disposed");
            }

            catch (ObjectDisposedException) { }
        }

        private static void Retirement(int count)
        {
            using var issuer = new QuicConnectionIdIssuer();
            var initial = issuer.Create();
            var announced = new List<Delivery>();
            using var ids = new LocalConnectionIds(issuer, initial, announced.Add);
            var first = ids.Snapshot;
            var initialToken = ids.InitialToken;
            Check(ids.Count == 1 && ReferenceEquals(first, ids.Snapshot), "initial immutable snapshot");
            initial[^1] ^= 1;
            Check(!ids.Contains(initial) && ids.Contains(first[0].Span), "initial storage is owned");
            ids.Activate();
            Check(ids.Count == 2 && announced.Count == 1, "one spare within minimum peer limit");
            ExpectProtocol(() => ids.Retire(1, first[0].Span), "queued ID is not yet sent");
            announced[0].Transmissions = 1;
            var current = ids.Snapshot;
            ExpectProtocol(() => ids.Retire(0, current[0].Span), "packet cannot retire its own destination");
            ids.Retire(0, current[1].Span);
            Check(initialToken.IndexOfAnyExcept((byte)0) < 0, "retired raw token cleared");
            Check(!ids.Contains(first[0].Span) && ids.Count == 2, "retired ID removed and replaced");
            Check(
                !ReferenceEquals(first, ids.Snapshot) && ReferenceEquals(ids.Snapshot, ids.Snapshot),
                "snapshot changes only with IDs"
            );
            ulong oldest = 1;

            for (var iteration = 0; iteration < count; iteration++)
            {
                var delivery = announced[^1];
                delivery.Transmissions = 1;
                var prior = ids.Snapshot;
                var retiring = announced[^2];
                ids.Retire(oldest, prior[1].Span);
                Check(retiring.Acknowledged, "retired announcement stops retransmission");

                Check(ids.Count == 2 && ids.NextSequence == (ulong)iteration + 4, "long-lived fixed retained state");
                var next = ids.Snapshot;
                ids.Retire(oldest, next[0].Span);
                Check(ReferenceEquals(next, ids.Snapshot), "duplicate retirement is idempotent");
                oldest++;

                if (announced.Count > 2) announced.RemoveRange(0, announced.Count - 2);
            }

            var last = ids.Snapshot;
            ExpectProtocol(() => ids.Retire(ids.NextSequence, last[0].Span), "unissued sequence rejected");
            ids.Dispose();
            Check(
                ids.Count == 0 && last.Count == 2 && first.Count == 1,
                "disposed state releases ownership and preserves immutable metadata"
            );
            Check(issuer.Token(last[0].Span).Length == 16, "borrower does not dispose listener issuer");
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            using var issuer = new QuicConnectionIdIssuer();
            var pending = new List<Delivery>();
            using var ids = new LocalConnectionIds(issuer, issuer.Create(), pending.Add);
            ids.Activate();
            ulong low = 0, high = 1;

            foreach (var command in data[..Math.Min(data.Length, 32)])
            {
                foreach (var delivery in pending) delivery.Transmissions = 1;

                var snapshot = ids.Snapshot;
                var chosen = (command & 1) == 0 ? low : high;
                var index = (command & 1) == 0 ? 0 : 1;

                if ((command & 2) != 0)
                    ExpectProtocol(() => ids.Retire(ids.NextSequence, snapshot[0].Span), "mutation future sequence");

                if ((command & 4) != 0)
                    ExpectProtocol(() => ids.Retire(chosen, snapshot[index].Span), "mutation current destination");

                ids.Retire(chosen, snapshot[1 - index].Span);
                low = index == 0 ? high : low;
                high = ids.NextSequence - 1;
                Check(ids.Count == 2 && ids.Snapshot.Count == 2, "mutation retained ID bound");
                pending.RemoveAll(static delivery => delivery.Acknowledged);
                Check(pending.Count <= 2, "mutation outstanding announcements bounded");
            }
        }

        private static void FramesAndLoss()
        {
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var keys = PacketKeys.Initial(original, false);
            var first = NativeFuzz.Initial(keys, original, client, 0, [1]);

            (byte[] Frame, ulong Error)[] invalidRetirements =
            [
                (Wire.Frame(0x19, 0), 0x0aUL),
                (Wire.Frame(0x19, 2), 0x0aUL),
                ([0x19, 0x40], 7UL)
            ];

            foreach (var (frame, error) in invalidRetirements)
            {
                using var connection = new QuicConnection(
                    first,
                    static _ => new ConnectionCoreTests.ModelTls(true),
                    static (port, _) => new StreamEchoApplication(port)
                );
                var sent = connection.Receive(first, 0);
                sent.AddRange(connection.Tick(100));
                sent.AddRange(connection.Tick(110));
                var snapshot = connection.IssuedConnectionIds;
                var announcement = sent
                    .Where(static packet => packet[0] < 128)
                    .Select(Plain)
                    .Single(static plaintext => plaintext[0] == 0x18);
                var repeated = connection.Tick(2000);
                Check(
                    repeated
                        .Where(static packet => packet[0] < 128)
                        .Select(Plain)
                        .Any(plaintext => plaintext.SequenceEqual(announcement)),
                    "lost announcement retransmits unchanged"
                );
                Check(
                    ReferenceEquals(snapshot, connection.IssuedConnectionIds) && snapshot.Count == 2,
                    "loss does not issue additional IDs"
                );
                var output = connection.Receive(
                    ConnectionCoreTests.ApplicationPacket(connection.ServerId, frame),
                    2001
                );
                Check(connection.Phase == ConnectionPhase.Closing, "invalid retirement closes connection");
                var close = Plain(output.Single());
                var reader = new WireReader(close);
                Check(reader.Integer() == 0x1c && reader.Integer() == error, "invalid retirement wire error");
            }
        }

        private static byte[] Plain(byte[] packet)
        {
            using var keys = new PacketKeys(new byte[32]);
            return keys.Unprotect(packet, 9, 0, out _);
        }

        internal static async Task DispatcherAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            double now = 0;
            var started = new TaskCompletionSource<QuicConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var factoryCalls = 0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, clock, owner, _) =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    var connection = new QuicConnection(
                        packet,
                        static _ => new ConnectionCoreTests.ModelTls(true),
                        static (port, _) => new StreamEchoApplication(port),
                        clock,
                        connectionIds: owner.ConnectionIds
                    );
                    started.SetResult(connection);
                    return connection;
                },
                () => Volatile.Read(ref now),
                capacity: 4,
                workers: 1,
                outputPacketCapacity: 256,
                outputByteCapacity: 16384
            );
            var peer = new IPEndPoint(IPAddress.Loopback, 19001);
            var local = new IPEndPoint(IPAddress.Loopback, 19002);
            var packet = new byte[128];
            packet[0] = 0x40;
            var sent = new List<OutboundDatagram>();

            for (var i = 0; i < 64; i++)
            {
                Check(dispatcher.Receive(packet, peer, local), "reset within global rate allowance");

                if (i == 0) peer.Port = 19003;

                Check(
                    await dispatcher
                        .SendOneAsync(datagram =>
                        {
                            sent.Add(datagram);
                            return ValueTask.CompletedTask;
                        })
                        .ConfigureAwait(false),
                    "queued reset sends"
                );

                if (i == 0)
                {
                    Check(sent[0].Peer.Port == 19001, "reset owns copied destination endpoint");
                    peer.Port = 19001;
                }
            }

            Check(
                !dispatcher.Receive(packet, peer, local) && factoryCalls == 0 && dispatcher.Active == 0,
                "reset rate bound without TLS admission"
            );
            Check(
                sent.Count == 64 && sent.All(static datagram => datagram.Bytes.Length == 43),
                "bounded stateless responses"
            );
            Volatile.Write(ref now, 1000);
            var accepted = 0;

            for (var i = 0; i < 80; i++) if (dispatcher.Receive(packet, peer, local)) accepted++;

            Check(
                accepted == 16 && dispatcher.PendingOutputPackets == 16 && dispatcher.PendingOutputBytes == 16 * 43,
                "reset output reserves ordinary queue capacity"
            );
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var initialKeys = PacketKeys.Initial(original, false);
            var first = NativeFuzz.Initial(initialKeys, original, client, 0, [1]);
            Check(dispatcher.Receive(first, peer, local), "ordinary handshake admitted behind bounded reset burst");
            var connection = await started.Task
                .WaitAsync(stop.Token)
                .ConfigureAwait(false);

            while (dispatcher.Accepted != 1)
            {
                dispatcher.Tick();
                await Task
                    .Delay(1, stop.Token)
                    .ConfigureAwait(false);
            }

            Check(
                dispatcher.IssuedRoutes == 2 && dispatcher.PendingOutputPackets > 16,
                "both aliases and ordinary output survive reset burst"
            );
            var before = connection.ReceivedBytes;
            Check(
                dispatcher.Receive(
                    ConnectionCoreTests.ApplicationPacket(connection.ServerId, Wire.Frame(1)),
                    peer,
                    local
                ),
                "known CID bypasses reset response rate limit"
            );

            while (connection.ReceivedBytes == before)
                await Task
                    .Delay(1, stop.Token)
                    .ConfigureAwait(false);

            Check(connection.Phase == ConnectionPhase.Active, "active connection survives reset burst");
            await dispatcher
                .DisposeAsync()
                .ConfigureAwait(false);
            Check(
                dispatcher.Active == 0
                    && dispatcher.IssuedRoutes == 0
                    && dispatcher.PendingOutputPackets == 0
                    && dispatcher.PendingOutputBytes == 0,
                "reset output and all issued aliases release on disposal"
            );
        }

        private static void ExpectProtocol(Action action, string label)
        {
            try
            {
                action();
            }

            catch (TransportException error) when (error.ErrorCode == 0x0a)
            {
                return;
            }

            throw new QuicFuzzFailure("local-id-" + label);
        }

        private static void Check(bool value, string label) => QuicFuzzChecks.Require(value, "local-id-" + label);
    }
}
