using System.Net;

namespace EmbedIO.Quic
{
    internal static class StatelessResetTests
    {
        private static readonly byte[] Client = [9, 10, 11, 12, 13, 14, 15, 16];

        private sealed class NotificationApplication(Action closed, Action? datagram = null) : StreamApplication
        {
            internal override void ConnectionClosed(ulong error, bool application) => closed();

            internal override void Stream(ulong id, byte[] bytes, bool fin) { }

            internal override void Reset(ulong id, ulong error, ulong reliableSize) { }

            internal override void ReceiveDatagram(byte[] bytes) => datagram?.Invoke();

            internal override bool CanRetire(ulong id) => true;

            internal override void Retire(ulong id) { }

            public override void Dispose() { }
        }

        private static byte[] First(byte owner)
        {
            byte[] original = [owner, 2, 3, 4, 5, 6, 7, 8];
            using var keys = PacketKeys.Initial(original, false);
            return NativeFuzz.Initial(keys, original, Client, 0, [1]);
        }

        private static QuicConnection Create(Action closed, ulong authenticationLimit = 1UL << 52)
        {
            var first = First(1);
            var result = new QuicConnection(
                first,
                static _ => new ConnectionCoreTests.ModelTls(true),
                (_, _) => new NotificationApplication(closed),
                authenticationLimit: authenticationLimit
            );
            result.Receive(first, 0);
            result.Tick(100);
            return result;
        }

        internal static byte[] LearnFrame(ReadOnlySpan<byte> token)
        {
            using var frame = new MemoryStream();
            frame.Write(Wire.Frame(0x18, 0, 0));
            frame.WriteByte((byte)Client.Length);
            frame.Write(Client);
            frame.Write(token);
            return frame.ToArray();
        }

        private static void Learn(QuicConnection connection, ReadOnlySpan<byte> token) =>
            connection.Receive(ConnectionCoreTests.ApplicationPacket(connection.ServerId, LearnFrame(token)), 101);

        internal static byte[] ResetBytes(ReadOnlySpan<byte> token, byte first = 0x40, int length = 64)
        {
            var result = new byte[length];
            result
                .AsSpan()
                .Fill(0x25);
            result[0] = first;
            token.CopyTo(result.AsSpan(length - 16));
            return result;
        }

        internal static void Run()
        {
            Eligibility();
            PacketPrecedence();
            CoalescedAndLimited();

            foreach (var first in new byte[] { 0, 0x40, 0xc0, 0xff })
            {
                var calls = 0;
                using var connection = Create(() => calls++);
                byte[] token =
                [
                    .. Enumerable
                        .Range(0, 16)
                        .Select(static value => (byte)(value + 61))
                ];
                Learn(connection, token);
                var packet = ResetBytes(token, first);

                if (first == 0x40) connection.ServerId.CopyTo(packet.AsSpan(1));

                Check(
                    connection.Receive(packet, 102).Count == 0
                        && connection.StatelessResetReceived
                        && connection.Phase == ConnectionPhase.Draining
                        && calls == 1,
                    "silent reset/header forms"
                );
                Check(connection.Receive(packet, 103).Count == 0 && calls == 1, "duplicate reset is once-only");
                Check(connection.Tick(100000).Count == 0 && connection.Closed, "reset drain cannot send or extend");
            }

            Mutate([1, 0, 3, 4, 7, 2, 8]);
        }

        private static void Eligibility()
        {
            var firstToken = new byte[16];
            byte[] nextToken =
            [
                1,
                .. new byte[15]
            ];
            using var ids = new PeerConnectionIds(Client, static value => new Delivery(2, Wire.Frame(0x19, value)));
            ids.Add(0, 0, Client, firstToken);
            Check(ids.ResetToken.IsEmpty && !ids.MatchesReset(ResetBytes(firstToken)), "unused initial ID");
            ids.MarkUsed();
            var snapshot = ids.ResetToken;
            Check(
                ids.MatchesReset(ResetBytes(firstToken)) && snapshot.Length == 16 && snapshot.Equals(ids.ResetToken),
                "used token and stable snapshot"
            );
            ids.Add(1, 0, new byte[8], nextToken);
            Check(!ids.MatchesReset(ResetBytes(nextToken)), "unused spare token");
            ids.Add(
                2,
                1,
                [
                    2,
                    .. new byte[7]
                ],
                [
                    2,
                    .. new byte[15]
                ]
            );
            Check(
                !ids.MatchesReset(ResetBytes(firstToken))
                    && !ids.MatchesReset(ResetBytes(nextToken))
                    && ids.ResetToken.IsEmpty,
                "retired and not-yet-used replacement"
            );
            ids.MarkUsed();
            Check(
                ids.MatchesReset(ResetBytes(nextToken)) && !ids.MatchesReset(ResetBytes(nextToken, length: 20)),
                "replacement use and minimum length"
            );
            Check(snapshot.Span.SequenceEqual(firstToken), "published snapshots remain immutable after retirement");
        }

        private static void PacketPrecedence()
        {
            var calls = 0;
            using var connection = Create(() => calls++);
            var valid = ConnectionCoreTests.ApplicationPacket(connection.ServerId, Wire.Frame(1), 1);
            var token = valid
                .AsSpan(valid.Length - 16)
                .ToArray();
            Learn(connection, token);
            var forged = ResetBytes(token);
            forged[^1] ^= 1;
            connection.Receive(forged, 102);
            connection.Receive(ResetBytes(token, length: 20), 103);
            Check(connection.Phase == ConnectionPhase.Active && calls == 0, "forged and short datagrams");
            connection.Receive(valid, 104);
            Check(!connection.StatelessResetReceived && calls == 0, "valid AEAD packet precedes token match");
            valid[^17] ^= 1;
            Check(
                connection.Receive(valid, 105).Count == 0 && connection.StatelessResetReceived && calls == 1,
                "failed AEAD with valid token resets"
            );
        }

        private static void CoalescedAndLimited()
        {
            byte[] token =
            [
                31,
                .. new byte[15]
            ];

            using (var connection = Create(static () => { }))
            {
                Learn(connection, token);
                byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
                using var keys = PacketKeys.Initial(original, false);
                var first = NativeFuzz.Initial(keys, original, Client, 1, [1]);
                byte[] coalesced =
                [
                    .. first,
                    .. ResetBytes(token)
                ];
                connection.Receive(coalesced, 102);
                Check(
                    connection.Phase == ConnectionPhase.Active && !connection.StatelessResetReceived,
                    "successfully processed first packet may skip token check"
                );
                Check(
                    connection.Receive(ResetBytes(token, length: 21), 103).Count == 0
                        && connection.StatelessResetReceived,
                    "minimum-size reset after valid coalesced packet"
                );
            }

            using (var connection = Create(
                static () => { },
                authenticationLimit: 1
            ))
            {
                Learn(connection, token);
                var invalid = ConnectionCoreTests.ApplicationPacket(connection.ServerId, Wire.Frame(1), 1);
                invalid[^1] ^= 1;
                connection.Receive(invalid, 102);
                Check(connection.Phase == ConnectionPhase.Closing, "AEAD limit starts closing");
                Check(
                    connection.Receive(ResetBytes(token), 103).Count == 0
                        && connection.StatelessResetReceived
                        && connection.Phase == ConnectionPhase.Draining,
                    "reset still stops closing after AEAD limit"
                );
            }
        }

        internal static void Mutate(ReadOnlySpan<byte> input)
        {
            using var ids = new PeerConnectionIds(
                Client,
                static value => new Delivery(2, Wire.Frame(0x19, value)) { Acknowledged = true }
            );

            for (var index = 0; index < Math.Min(input.Length, 16); index++)
            {
                var sequence = (ulong)index + 1;
                byte[] identity =
                [
                    (byte)sequence,
                    .. new byte[7]
                ];
                byte[] token =
                [
                    (byte)sequence,
                    .. new byte[15]
                ];
                ids.Add(sequence, sequence, identity, token);
                Check(!ids.MatchesReset(ResetBytes(token)) && ids.ResetToken.IsEmpty, "mutation unselected token");
                var used = (input[index] & 1) != 0;

                if (used) ids.MarkUsed();

                var packet = ResetBytes(token, input[index], (input[index] & 4) == 0 ? 64 : 20);
                var corrupt = (input[index] & 2) != 0;

                if (corrupt) packet[^1] ^= 1;

                Check(ids.MatchesReset(packet) == (used && !corrupt && packet.Length >= 21), "mutation token oracle");
                ids.Prune();
                Check(
                    ids.ActiveCount == 1 && ids.RetainedRetirements == 0 && ids.ResetToken.Length <= 16,
                    "mutation fixed state bounds"
                );
            }
        }

        internal static async Task DispatcherAsync()
        {
            await DispatcherCaseAsync(false).ConfigureAwait(false);
            await DispatcherCaseAsync(true).ConfigureAwait(false);
            await DispatcherCaseAsync(false, true).ConfigureAwait(false);
        }

        private static async Task DispatcherCaseAsync(bool expireFirst, bool collision = false)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var closed = new[]
            {
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var datagramReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new[]
            {
                new TaskCompletionSource<QuicConnection>(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource<QuicConnection>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var created = -1;
            double now = 0;
            await using var dispatcher = new QuicConnectionDispatcher(
                (packet, clock, _, _) =>
                {
                    var index = Interlocked.Increment(ref created);
                    var connection = new QuicConnection(
                        packet,
                        _ => new ConnectionCoreTests.ModelTls(!expireFirst || index != 0),
                        (_, _) => new NotificationApplication(() => closed[index].TrySetResult(), () =>
                        {
                            Check(index == 1, "authenticated datagram reaches its header owner");
                            datagramReceived.TrySetResult();
                        }),
                        clock
                    );
                    started[index].SetResult(connection);
                    return connection;
                },
                () => Volatile.Read(ref now),
                capacity: 2,
                workers: 1
            );
            var peer = new IPEndPoint(IPAddress.Loopback, 17001);
            var local = new IPEndPoint(IPAddress.Loopback, 18001);

            async Task Flush()
            {
                await dispatcher
                    .WaitForOutputAsync(stop.Token)
                    .ConfigureAwait(false);

                while (
                    await dispatcher
                        .SendOneAsync(static _ => ValueTask.CompletedTask)
                        .ConfigureAwait(false)
                ) { }
            }

            var connections = new QuicConnection[2];

            for (var index = 0; index < 2; index++)
            {
                Check(dispatcher.Receive(First((byte)(index + 1)), peer, local), "shared endpoint initial");
                connections[index] = await started[index].Task
                    .WaitAsync(stop.Token)
                    .ConfigureAwait(false);
                await Flush().ConfigureAwait(false);
            }

            Volatile.Write(ref now, 100);
            dispatcher.Tick();

            // Flush the first application packets before announcing peer tokens.
            while (
                await dispatcher
                    .SendOneAsync(static _ => ValueTask.CompletedTask)
                    .ConfigureAwait(false)
            ) { }

            var valid = ConnectionCoreTests.ApplicationPacket(connections[1].ServerId, [0x30, 1], 1);

            for (var index = expireFirst ? 1 : 0; index < 2; index++)
            {
                byte[] token =
                [
                    (byte)(index + 31),
                    .. new byte[15]
                ];

                if (collision && index == 0)
                    token = valid
                        .AsSpan(valid.Length - 16)
                        .ToArray();

                Check(
                    dispatcher.Receive(
                        ConnectionCoreTests.ApplicationPacket(connections[index].ServerId, LearnFrame(token)),
                        peer,
                        local
                    ),
                    "learn shared endpoint token"
                );
                await Flush().ConfigureAwait(false);

                while (dispatcher.ResetRoutes < index + 1 - (expireFirst ? 1 : 0))
                {
                    dispatcher.Tick();

                    while (
                        await dispatcher
                            .SendOneAsync(static _ => ValueTask.CompletedTask)
                            .ConfigureAwait(false)
                    ) { }

                    await Task
                        .Delay(1, stop.Token)
                        .ConfigureAwait(false);
                }
            }

            Check(
                dispatcher.ResetRoutes == (expireFirst ? 1 : 2) && dispatcher.EndpointGroups == 1,
                "bounded shared endpoint index"
            );
            var target = expireFirst ? 1 : 0;
            byte[] selected =
            [
                (byte)(target + 31),
                .. new byte[15]
            ];

            if (collision)
            {
                selected = valid
                    .AsSpan(valid.Length - 16)
                    .ToArray();
                Check(dispatcher.Receive(valid, peer, local), "authenticated shared-endpoint packet queued");
                await Task
                    .WhenAny(datagramReceived.Task, closed[0].Task)
                    .WaitAsync(stop.Token)
                    .ConfigureAwait(false);
                Check(
                    datagramReceived.Task.IsCompleted && !closed[0].Task.IsCompleted && !closed[1].Task.IsCompleted,
                    "authenticated sibling packet precedes another owner token match"
                );
                await Flush().ConfigureAwait(false);
            }

            var reset = ResetBytes(selected);
            connections[1 - target].ServerId.CopyTo(reset.AsSpan(1));
            Check(!dispatcher.Receive(reset, new IPEndPoint(IPAddress.Loopback, 17003), local), "foreign reset source");
            Check(
                !dispatcher.Receive(reset, peer, new IPEndPoint(IPAddress.Loopback, 18003)),
                "foreign reset destination"
            );

            // Expire the alias named by the randomized header inside Receive,
            // while the token identifies the established sibling connection.
            if (expireFirst) Volatile.Write(ref now, 4000);

            Check(dispatcher.Receive(reset, peer, local), "token selects owner despite sibling header CID");
            await closed[target].Task
                .WaitAsync(stop.Token)
                .ConfigureAwait(false);
            Check(
                connections[target].StatelessResetReceived
                    && !closed[1 - target].Task.IsCompleted
                    && !connections[1 - target].StatelessResetReceived,
                "reset affects exactly the token owner"
            );
            Check(
                !await dispatcher
                    .SendOneAsync(static _ => ValueTask.CompletedTask)
                    .ConfigureAwait(false),
                "no reset response"
            );

            if (!expireFirst)
            {
                Check(
                    dispatcher.Receive(
                        ConnectionCoreTests.ApplicationPacket(connections[1].ServerId, Wire.Frame(1), 2),
                        peer,
                        local
                    ),
                    "healthy sibling accepts following packet"
                );
                await Flush().ConfigureAwait(false);
            }

            await dispatcher
                .DisposeAsync()
                .ConfigureAwait(false);
            Check(
                dispatcher.Active == 0
                    && dispatcher.ResetRoutes == 0
                    && dispatcher.EndpointGroups == 0
                    && dispatcher.Accepted == dispatcher.Retired,
                "reset index and peer groups retire with owners"
            );
        }

        private static void Check(bool condition, string name) =>
            QuicFuzzChecks.Require(condition, "stateless-reset-" + name);
    }
}
