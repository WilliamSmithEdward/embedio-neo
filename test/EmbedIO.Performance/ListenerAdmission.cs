using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using EmbedIO;

// Measures managed listener admission and accept under concurrent registration.
// Producer threads stand in for connections that have parsed a request head;
// one accept loop stands in for WebServer.ProcessRequestsAsync. No sockets are
// opened, so the numbers isolate the listener queue and its synchronization.
internal static class ListenerAdmission
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--listener-admission", StringComparer.Ordinal)) return false;
        var seconds = ReadOption(args, "--seconds", 1.0);
        var rounds = (int)ReadOption(args, "--rounds", 5);
        var depth = (int)ReadOption(args, "--depth", 256);
        var contextType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpListenerContext", true)
            ?? throw new InvalidOperationException("Missing HttpListenerContext.");
        var id = contextType.GetField("<Id>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing context identifier field.");
        var register = (typeof(EmbedIO.Net.HttpListener).GetMethod("RegisterContext", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing RegisterContext."))
            .CreateDelegate<Action<EmbedIO.Net.HttpListener, IHttpContextImpl>>();
        Console.WriteLine(FormattableString.Invariant($"processors={Environment.ProcessorCount} seconds={seconds} rounds={rounds} depth={depth}"));
        foreach (var producers in new[] { 1, 4, 8, 16 })
        {
            var rows = new List<Sample>();
            for (var round = 0; round < rounds + 1; round++)
            {
                var sample = Measure(producers, depth, seconds, contextType, id, register);
                if (round > 0) rows.Add(sample); // The first round warms up the JIT and thread pool.
            }
            rows.Sort((left, right) => left.RequestsPerSecond.CompareTo(right.RequestsPerSecond));
            var median = rows[rows.Count / 2];
            Console.WriteLine(FormattableString.Invariant(
                $"listener-admission-{producers}: {median.RequestsPerSecond:F0} req/s, {median.CpuMicroseconds:F2} cpu-us/req, {median.Contentions:F3} lock-contentions/req, {median.Bytes:F0} B/req (min {rows[0].RequestsPerSecond:F0}, max {rows[^1].RequestsPerSecond:F0})"));
        }
        return true;
    }

    private static Sample Measure(int producers, int depth, double seconds, Type contextType, FieldInfo id, Action<EmbedIO.Net.HttpListener, IHttpContextImpl> register)
    {
        using var listener = new EmbedIO.Net.HttpListener();
        listener.Start(); // No prefixes: exercise admission without opening sockets.
        var pools = new ConcurrentQueue<IHttpContextImpl>[producers];
        // A producer blocks until the consumer returns one of its contexts, like a
        // connection waiting for its response. Spinning with sleeps would measure timer ticks.
        var available = Enumerable.Range(0, producers).Select(_ => new SemaphoreSlim(depth)).ToArray();
        var owners = new Dictionary<IHttpContextImpl, int>(ReferenceEqualityComparer.Instance);
        for (var producer = 0; producer < producers; producer++)
        {
            pools[producer] = new ConcurrentQueue<IHttpContextImpl>();
            for (var index = 0; index < depth; index++)
            {
                var context = (IHttpContextImpl)RuntimeHelpers.GetUninitializedObject(contextType);
                id.SetValue(context, FormattableString.Invariant($"{producer}-{index}"));
                owners.Add(context, producer);
                pools[producer].Enqueue(context);
            }
        }

        using var stop = new CancellationTokenSource();
        long accepted = 0;
        var consumer = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var context = await listener.GetContextAsync(stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref accepted);
                    var owner = owners[context];
                    pools[owner].Enqueue(context);
                    _ = available[owner].Release();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var running = 1;
        var threads = Enumerable.Range(0, producers).Select(producer => new Thread(() =>
        {
            var pool = pools[producer];
            while (Volatile.Read(ref running) != 0)
            {
                if (!available[producer].Wait(50)) continue;
                if (!pool.TryDequeue(out var context)) throw new InvalidOperationException("A returned context was missing.");
                register(listener, context);
            }
        })
        { IsBackground = true }).ToArray();

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        foreach (var thread in threads) thread.Start();
        Thread.Sleep(100); // Reach steady state before the measured window.
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var contentionBefore = Monitor.LockContentionCount;
        var bytesBefore = GC.GetTotalAllocatedBytes(true);
        var countBefore = Interlocked.Read(ref accepted);
        var clock = Stopwatch.StartNew();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var count = Interlocked.Read(ref accepted) - countBefore;
        var elapsed = clock.Elapsed.TotalSeconds;
        var bytes = GC.GetTotalAllocatedBytes(true) - bytesBefore;
        var contentions = Monitor.LockContentionCount - contentionBefore;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds * 1000;
        Volatile.Write(ref running, 0);
        foreach (var thread in threads) thread.Join();
        // Accept every queued context before disposal; these placeholders have no connection to close.
        var drained = Stopwatch.StartNew();
        while (pools.Sum(pool => pool.Count) != producers * depth)
        {
            if (drained.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Queued contexts were not accepted.");
            Thread.Sleep(1);
        }
        stop.Cancel();
        consumer.GetAwaiter().GetResult();
        foreach (var gate in available) gate.Dispose();
        if (count == 0) throw new InvalidOperationException("No contexts were accepted.");
        return new Sample(count / elapsed, cpu / count, contentions / (double)count, bytes / (double)count);
    }

    private static double ReadOption(string[] args, string name, double fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length
            ? double.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture)
            : fallback;
    }

    private readonly record struct Sample(double RequestsPerSecond, double CpuMicroseconds, double Contentions, double Bytes);
}
