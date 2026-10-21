namespace EmbedIO.Quic
{
    internal static class StreamWindowTests
    {
        internal static void Run()
        {
            EmptyOwnerAllocation();
            StreamStorageTests.Run();
            using var window = new StreamReassembly(32);

            for (ulong offset = 0; offset < 32000; offset += 32)
            {
                var bytes = Enumerable
                    .Range(0, 32)
                    .Select(i => (byte)(offset + (ulong)i))
                    .ToArray();
                window.Add(offset + 16, bytes.AsSpan(16));
                Check(window.Consume().Length == 0, "gap retains bytes");
                window.Add(offset, bytes.AsSpan(0, 16));
                Check(
                    window
                        .Consume()
                        .AsSpan()
                        .SequenceEqual(bytes),
                    "wrapped receive window preserves bytes"
                );
                window.Add(offset, bytes);
                Check(window.Consume().Length == 0, "retired duplicate cannot reappear");
            }

            window.Add(32000, [], true);
            Check(window.Complete && window.Delivered == 32000, "final size survives wrap");
            Reject(() => window.Add(31999, [], true), "conflicting final size after retirement");
            Reject(() => window.Add(32000, [1]), "data past final size");
            using var sparseWindow = new StreamReassembly(16);
            sparseWindow.Add(8, [1, 2]);
            Reject(() => sparseWindow.Add(8, [2]), "buffered overlap must agree");
            Reject(() => sparseWindow.Add(8, [], true), "final size before sparse data");
            Reject(() => sparseWindow.Add(16, [1]), "bounded sparse window");
            Reject(() => sparseWindow.Add(StreamReassembly.MaximumOffset, [1]), "offset overflow");
            sparseWindow.Add(0, new byte[8]);
            Check(sparseWindow.Consume(4).Length == 4 && sparseWindow.Delivered == 4, "reliable prefix boundary");
            Check(
                sparseWindow.Consume(2).Length == 0 && sparseWindow.Delivered == 4,
                "smaller reliable boundary never rewinds"
            );
        }

        private static void EmptyOwnerAllocation()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var owners = new StreamReassembly[144];

            for (var index = 0; index < owners.Length; index++) owners[index] = new StreamReassembly(65536);

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            foreach (var owner in owners) owner.Dispose();

            GC.KeepAlive(owners);
            Check(allocated < 65536, "empty owners do not reserve payload windows");
        }

        private static void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException("Stream window regression: " + name);
        }

        private static void Reject(Action action, string name)
        {
            try
            {
                action();
            }

            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException("Expected stream window rejection: " + name);
        }
    }
}
