namespace EmbedIO.Quic
{
    internal static class RecoveryRetentionTests
    {
        internal static void Run()
        {
            var failures = new List<string>();

            (string Name, Action Test)[] tests =
            [
                ("acknowledged-history", Acknowledgments),
                ("lost-history", Losses)
            ];

            foreach (var (name, test) in tests)
            {
                try
                {
                    test();
                }

                catch (InvalidDataException error)
                {
                    failures.Add(name + ": " + error.Message);
                }
            }

            if (failures.Count != 0)
                throw new InvalidOperationException("Recovery retention: " + string.Join("; ", failures));
        }

        private static void Acknowledgments()
        {
            var recovery = new Recovery((_, _) => { }) { HandshakeConfirmed = true };

            for (ulong number = 0; number < 50000; number++)
            {
                var item = new Delivery(2, [1]);
                recovery.SentPacket(2, number, 1000, number * 10, item, false);
                recovery.Acknowledge(2, [(number, number)], 0, number * 10 + 1);
                Bounds(recovery);
                QuicFuzzChecks.Require(
                    item.Acknowledged && recovery.BytesInFlight == 0,
                    "sustained-acknowledgment-accounting"
                );
            }

            for (ulong offset = 0; offset < 4; offset++)
                recovery.SentPacket(
                    2,
                    50000 + offset,
                    1000,
                    600000 + (offset == 0 ? 0 : offset == 1 ? 100 : 108 + offset),
                    new Delivery(2, [1]),
                    false
                );

            recovery.Acknowledge(2, [(50003UL, 50003UL)], 0, 600111);
            QuicFuzzChecks.Require(recovery.CongestionWindow == 2400, "persistent-congestion-after-history-compaction");
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            data = data[..Math.Min(data.Length, 64)];

            if (data.Length == 0) return;

            var ackLimit = 8 + data[0] % 17;
            var retiredLimit = 2 + data[0] % 15;

            static void Ignore(Delivery _, bool __) { }

            var recovery = new Recovery(Ignore, ackLimit, retiredLimit) { HandshakeConfirmed = true };
            var lost = new Queue<ulong>();
            var items = new List<(ulong Number, Delivery Item)>();

            for (var index = 0; index < data.Length; index++)
            {
                var number = (ulong)index * 2;
                var item = new Delivery(2, [1]);
                items.Add((number, item));
                var now = index * 100.0;
                recovery.SentPacket(2, number, 1000, now, item, false);
                recovery.SentPacket(2, number + 1, 1000, now + 49, new Delivery(2, [1]), false);
                var drop = (data[index] & 1) != 0;
                recovery.Acknowledge(2, drop ? [(number + 1, number + 1)] : [(number, number + 1)], 0, now + 50);

                if (drop)
                {
                    lost.Enqueue(number);

                    if (lost.Count > retiredLimit) lost.Dequeue();
                }

                QuicFuzzChecks.Require(item.Acknowledged == !drop, "history-model-current-ack");
                var (priorNumber, priorItem) = items[data[index] % items.Count];
                var expected = priorItem.Acknowledged || lost.Contains(priorNumber);
                recovery.Acknowledge(2, [(priorNumber, priorNumber)], 0, now + 51);
                QuicFuzzChecks.Require(
                    priorItem.Acknowledged == expected && recovery.BytesInFlight == 0,
                    "history-model-retired-ack"
                );
                QuicFuzzChecks.Require(
                    recovery.AcknowledgmentCount <= ackLimit
                        && recovery.RetiredPacketCount <= retiredLimit
                        && recovery.ActivePacketCount == 0,
                    "history-model-resource-bounds"
                );
            }
        }

        private static void Bounds(Recovery recovery) =>
            QuicFuzzChecks.Require(
                recovery.ActivePacketCount <= Recovery.MaximumActivePackets
                    && recovery.RetiredPacketCount <= Recovery.MaximumRetiredPackets
                    && recovery.AcknowledgmentCount <= Recovery.MaximumAcknowledgments
                    && recovery.LossHistoryCount <= 4096,
                "recovery-history-bounds"
            );

        private static void Losses()
        {
            var recovery = new Recovery((_, _) => { }) { HandshakeConfirmed = true };
            Delivery? first = null;

            for (ulong index = 0; index < 5000; index++)
            {
                var number = index * 2;
                var item = new Delivery(2, [1]);
                first ??= item;
                recovery.SentPacket(2, number, 1000, index * 100, item, false);
                recovery.SentPacket(2, number + 1, 1000, index * 100 + 49, new Delivery(2, [1]), false);
                recovery.Acknowledge(2, [(number + 1, number + 1)], 0, index * 100 + 50);
                Bounds(recovery);
                QuicFuzzChecks.Require(recovery.BytesInFlight == 0, "sustained-loss-accounting");
            }

            var repeat = first ?? throw new InvalidOperationException("Missing original delivery.");
            recovery.SentPacket(2, 10000, 1000, 600000, repeat, false);
            recovery.Acknowledge(2, [(0UL, 0UL)], 0, 600001);
            QuicFuzzChecks.Require(
                !repeat.Acknowledged && recovery.BytesInFlight == 1000,
                "evicted-ack-does-not-release-current-flight"
            );
            recovery.Acknowledge(2, [(10000UL, 10000UL)], 0, 600002);
            QuicFuzzChecks.Require(
                repeat.Acknowledged && recovery.BytesInFlight == 0,
                "current-retransmission-acknowledged"
            );
        }
    }
}
