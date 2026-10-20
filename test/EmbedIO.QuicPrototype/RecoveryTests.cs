namespace EmbedIO.QuicPrototype
{
    internal static class RecoveryTests
    {
        internal static void Run()
        {
            var repeats = new List<(Delivery Delivery, bool Probe)>();
            Recovery Create() => new((delivery, probe) => repeats.Add((delivery, probe)));
            var recovery = Create();
            var first = new Delivery(0, [6]);
            recovery.SentPacket(0, 0, 1200, 0, first, false);
            recovery.Tick(998);
            Check(repeats.Count == 0, "initial PTO floor");
            recovery.Tick(999);
            Check(
                repeats.Count == 1 && repeats[0].Probe && recovery.LostPackets == 0,
                "PTO probes without declaring loss"
            );
            recovery.Tick(1000);
            Check(repeats.Count == 1, "PTO backoff");
            recovery.SentPacket(0, 1, 1200, 1000, first, false);
            recovery.Acknowledge(0, [(0UL, 0UL)], 0, 1001);
            Check(
                first.Acknowledged && recovery.BytesInFlight == 1200,
                "logical delivery and physical flight ownership"
            );
            recovery.Discard(0);
            Check(recovery.BytesInFlight == 0, "discard removes flight");

            repeats.Clear();
            recovery = Create();

            for (ulong i = 0; i < 4; i++) recovery.SentPacket(2, i, 1000, 0, new Delivery(2, [1]), false);

            recovery.Acknowledge(2, [(3UL, 3UL)], 0, 10);
            Check(recovery.LostPackets == 1 && repeats.Count == 1, "packet threshold");
            Check(recovery.CongestionEvents == 1 && recovery.CongestionWindow >= 2400, "NewReno reduction");
            var lost = repeats[0].Delivery;
            recovery.Acknowledge(2, [(0UL, 0UL)], 0, 11);
            Check(
                lost.Acknowledged && recovery.BytesInFlight == 2000,
                "late ACK cancels retransmission without subtracting flight twice"
            );
            recovery.Tick(20);
            Check(
                recovery.BytesInFlight == 0 && recovery.CongestionEvents == 1,
                "time threshold and one reduction per epoch"
            );
            recovery.Acknowledge(2, [(0UL, 3UL)], 0, 22);
            Check(recovery.BytesInFlight == 0, "duplicate and overlapping ACKs");

            repeats.Clear();
            recovery = Create();
            recovery.SentPacket(0, 0, 1200, 0, new Delivery(0, [6]), false);
            recovery.SentPacket(2, 0, 1000, 1, new Delivery(2, [1]), false);
            recovery.Acknowledge(2, [(0UL, 0UL)], 0, 20);
            Check(recovery.BytesInFlight == 1200 && recovery.LostPackets == 0, "packet-number-space separation");
            recovery = Create();

            for (ulong i = 0; i < 10; i++) recovery.SentPacket(2, i, 1200, i, new Delivery(2, [1]), false);

            Check(
                !recovery.CanSend(1200, 1000, false) && recovery.CanSend(1200, 1000, true),
                "congestion window and bounded PTO exception"
            );
            recovery = Create();
            recovery.SentPacket(2, 0, 1000, 0, new Delivery(2, [1]), false, true);
            recovery.Acknowledge(2, [(0UL, 0UL)], 0, 10);
            Check(recovery.CongestionWindow == 12000, "application-limited flight does not grow window");
            recovery = Create();
            recovery.HandshakeConfirmed = true;
            recovery.SentPacket(2, 0, 1000, 0, new Delivery(2, [1]), false);
            recovery.Acknowledge(2, [(0UL, 0UL)], 0, 100);
            recovery.SentPacket(2, 1, 1000, 101, new Delivery(2, [1]), false);
            recovery.Acknowledge(2, [(1UL, 1UL)], 100000, 226);
            Check(recovery.SmoothedRtt == 100, "ACK delay is bounded by advertised maximum");
            recovery = Create();
            recovery.HandshakeConfirmed = true;
            recovery.SentPacket(2, 0, 1000, 0, new Delivery(2, [1]), false);
            recovery.Acknowledge(2, [(0UL, 0UL)], 0, 10);
            recovery.SentPacket(2, 1, 1000, 100, new Delivery(2, [1]), false);
            recovery.SentPacket(2, 2, 1000, 500, new Delivery(2, [1]), false);
            recovery.SentPacket(2, 3, 1000, 1000, new Delivery(2, [1]), false);
            recovery.SentPacket(2, 4, 1000, 1190, new Delivery(2, [1]), false);
            recovery.Acknowledge(2, [(4UL, 4UL)], 0, 1200);
            Check(recovery.CongestionWindow == 2400, "persistent congestion crosses detection epochs");
            var random = new Random(9002);

            for (var run = 0; run < 100; run++)
            {
                recovery = Create();
                recovery.HandshakeConfirmed = true;
                repeats.Clear();
                double now = 0;

                for (ulong number = 0; number < 100; number++)
                {
                    now += random.Next(1, 15);
                    recovery.SentPacket(2, number, random.Next(40, 1201), now, new Delivery(2, [1]), false);

                    if (number != 0 && random.Next(3) != 0)
                    {
                        var ack = (ulong)random.NextInt64((long)number);
                        recovery.Acknowledge(2, [(ack, ack)], random.Next(100), now + 1);
                    }

                    recovery.Tick(now + 2);
                    Check(
                        recovery.BytesInFlight >= 0
                            && recovery.CongestionWindow >= 2400
                            && double.IsFinite(recovery.CongestionWindow),
                        "seeded recovery state invariants"
                    );
                }

                recovery.Discard(2);
                Check(recovery.BytesInFlight == 0, "seeded retirement releases all flight");
            }
        }

        private static void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("Recovery regression: " + name);
        }
    }
}
