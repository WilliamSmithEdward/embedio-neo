namespace EmbedIO.Quic
{
    internal static class QuicFuzzState
    {
        private sealed class Actor
        {
            internal readonly List<(Delivery Item, bool Probe)> Repeats = [];
            internal readonly List<(int Level, ulong Number, Delivery Item)> Packets = [];
            internal readonly ulong[] Next = new ulong[3];
            internal readonly ConnectionLifetime Lifetime = new() { IdleTimeout = 1000 };
            internal readonly Recovery Recovery;
            internal double Now;

            internal Actor() =>
                Recovery = new((item, probe) => Repeats.Add((item, probe))) { HandshakeConfirmed = true };

            internal string Snapshot() =>
                string.Join(
                    '|',
                    Recovery.BytesInFlight,
                    Recovery.CongestionWindow,
                    Recovery.LostPackets,
                    Recovery.ProbeTimeouts,
                    Recovery.RetransmittedPackets,
                    Repeats.Count,
                    Packets.Count,
                    Lifetime.Phase,
                    Lifetime.IdleExpired,
                    Now
                );
        }

        internal static void Run(ReadOnlySpan<byte> steps, bool lifetimeOnly, Action<string>? cover)
        {
            var actors = new[]
            {
                new Actor(),
                new Actor(),
                new Actor()
            };

            for (var cursor = 0; cursor + 2 < steps.Length && cursor < 384; cursor += 3)
            {
                var owner = steps[cursor] % 3;
                var actor = actors[owner];
                var command = steps[cursor + 1] % 12;
                var value = steps[cursor + 2];
                var snapshots = actors
                    .Select(a => a.Snapshot())
                    .ToArray();
                actor.Now += value % 32;

                if (lifetimeOnly) Lifetime(actor, command, value);

                else Recover(actor, command, value);

                cover?.Invoke((lifetimeOnly ? "lifetime-" : "recovery-") + command);

                for (var index = 0; index < actors.Length; index++)
                    if (index != owner)
                        QuicFuzzChecks.Require(
                            actors[index].Snapshot() == snapshots[index],
                            "connection-owner-isolation"
                        );
            }

            foreach (var actor in actors)
            {
                for (var level = 0; level < 3; level++) actor.Recovery.Discard(level);

                QuicFuzzChecks.Require(actor.Recovery.BytesInFlight == 0, "recovery-discard-accounting");
                actor.Lifetime.Drain(actor.Now, 300);
                actor.Lifetime.Tick(actor.Now + 100000, 300);
                actor.Lifetime.Authenticated(actor.Now + 100001);
                actor.Lifetime.Close(actor.Now + 100002, 300);
                QuicFuzzChecks.Require(
                    actor.Lifetime.Phase == ConnectionPhase.Closed
                        && !actor.Lifetime.TakeCloseResponse(actor.Now + 100002),
                    "closed-connection-stays-closed"
                );
            }
        }

        private static void Recover(Actor actor, int command, int value)
        {
            var recovery = actor.Recovery;
            var level = value % 3;

            if (command < 4 && !recovery.IsDiscarded(level))
            {
                var item = new Delivery(level, [(byte)value], command != 3, command != 2);
                var number = actor.Next[level]++;
                var size = 100 + value;

                if (recovery.CanSend(size, actor.Now, false))
                {
                    var before = recovery.BytesInFlight;
                    recovery.SentPacket(level, number, size, actor.Now, item, false, command == 1);
                    QuicFuzzChecks.Require(
                        recovery.BytesInFlight == before + (item.Eliciting ? size : 0),
                        "send-flight-accounting"
                    );
                    actor.Packets.Add((level, number, item));
                }
            }

            else if (command < 7 && actor.Packets.Count > 0)
            {
                var (Level, Number, Item) = actor.Packets[value % actor.Packets.Count];

                if (!recovery.IsDiscarded(Level))
                {
                    recovery.Acknowledge(Level, [(Number, Number)], value, actor.Now);
                    QuicFuzzChecks.Require(Item.Acknowledged, "ack-delivery-ownership");
                    var before = recovery.BytesInFlight;
                    recovery.Acknowledge(Level, [(Number, Number)], value, actor.Now);
                    QuicFuzzChecks.Require(recovery.BytesInFlight == before, "duplicate-ack-accounting");
                }
            }

            else if (command == 7) recovery.Discard(level);

            else
            {
                actor.Now += value * 100;
                recovery.Tick(actor.Now);

                foreach (var (item, probe) in actor.Repeats.ToArray())
                {
                    if (item.Acknowledged || recovery.IsDiscarded(item.Level) || item.Transmissions >= 4) continue;

                    if (!recovery.CanSend(100, actor.Now, probe)) continue;

                    var number = actor.Next[item.Level]++;
                    recovery.SentPacket(item.Level, number, 100, actor.Now, item, false);
                    actor.Packets.Add((item.Level, number, item));
                }

                actor.Repeats.Clear();
            }

            QuicFuzzChecks.Require(
                recovery.BytesInFlight >= 0
                    && recovery.BytesInFlight <= actor.Packets.Count * 355L
                    && double.IsFinite(recovery.CongestionWindow)
                    && recovery.CongestionWindow >= 2400
                    && double.IsFinite(recovery.SmoothedRtt)
                    && recovery.SmoothedRtt > 0,
                "recovery-resource-bounds"
            );
        }

        private static void Lifetime(Actor actor, int command, int value)
        {
            var lifetime = actor.Lifetime;
            var before = lifetime.Phase;

            switch (command % 6)
            {
                case 0:
                    lifetime.Authenticated(actor.Now);
                    break;

                case 1:
                    lifetime.SentEliciting(actor.Now);
                    break;

                case 2:
                    lifetime.Close(actor.Now, 300);
                    break;

                case 3:
                    lifetime.Drain(actor.Now, 300);
                    break;

                case 4:
                    actor.Now += value * 100;
                    lifetime.Tick(actor.Now, 300);
                    break;

                case 5:
                    var response = lifetime.TakeCloseResponse(actor.Now);
                    QuicFuzzChecks.Require(!response || before == ConnectionPhase.Closing, "close-response-phase");
                    QuicFuzzChecks.Require(!lifetime.TakeCloseResponse(actor.Now), "close-response-rate");
                    break;
            }

            QuicFuzzChecks.Require(lifetime.Phase >= before, "connection-phase-monotonic");

            if (lifetime.Phase is ConnectionPhase.Draining or ConnectionPhase.Closed)
                QuicFuzzChecks.Require(!lifetime.TakeCloseResponse(actor.Now), "draining-is-silent");
        }
    }
}
