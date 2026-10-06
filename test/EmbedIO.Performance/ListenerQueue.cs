using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using EmbedIO;

internal static class ListenerQueue
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--listener-queue", StringComparer.Ordinal)) return false;
        var contextType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpListenerContext", true)!;
        var id = contextType.GetField("<Id>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var size in new[] { 1, 16, 256 })
        {
            using var listener = new EmbedIO.Net.HttpListener();
            listener.Start(); // No prefixes: exercise the accept queue without opening sockets.
            var queue = (System.Collections.IDictionary)typeof(EmbedIO.Net.HttpListener)
                .GetField("_ctxQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(listener)!;
            var semaphore = (SemaphoreSlim)typeof(EmbedIO.Net.HttpListener)
                .GetField("_ctxQueueSem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(listener)!;
            var contexts = Enumerable.Range(0, size).Select(index =>
            {
                var context = RuntimeHelpers.GetUninitializedObject(contextType);
                id.SetValue(context, index.ToString());
                return (IHttpContextImpl)context;
            }).ToArray();
            void Batch()
            {
                foreach (var context in contexts) queue[context.Id] = context;
                semaphore.Release(size);
                for (var index = 0; index < size; index++)
                    GC.KeepAlive(listener.GetContextAsync(CancellationToken.None).GetAwaiter().GetResult());
                if (queue.Count != 0) throw new InvalidOperationException("Queue did not drain.");
            }
            for (var iteration = 0; iteration < 100; iteration++) Batch();
            var rows = new List<(double Ns, double Bytes)>();
            var batches = 4096 / size;
            for (var round = 0; round < 7; round++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                for (var iteration = 0; iteration < batches; iteration++) Batch();
                rows.Add((Stopwatch.GetElapsedTime(start).TotalNanoseconds / (batches * size),
                    (GC.GetAllocatedBytesForCurrentThread() - before) / (double)(batches * size)));
            }
            rows.Sort((left, right) => left.Ns.CompareTo(right.Ns));
            Console.WriteLine($"listener-queue-{size}: {rows[3].Ns:F1} ns/request, {rows[3].Bytes:F0} B/request");
            if (args.Contains("--verify-allocations", StringComparer.Ordinal) && rows[3].Bytes > 300)
                throw new InvalidOperationException("Listener queue exceeded its 300 B/request allocation budget.");
        }
        return true;
    }
}
