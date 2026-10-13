using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
        var rounds = ReadCount(args, "--rounds", 5, int.MaxValue - 1);
        var depth = ReadCount(args, "--depth", 256, int.MaxValue / 16);
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(args), "--seconds must be finite, positive and fit the supported sleep duration.");
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
        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, producers).Select(producer => new Thread(() =>
        {
            try
            {
                var pool = pools[producer];
                while (Volatile.Read(ref running) != 0)
                {
                    if (!available[producer].Wait(50)) continue;
                    if (!pool.TryDequeue(out var context)) throw new InvalidOperationException("A returned context was missing.");
                    register(listener, context);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Enqueue(error);
                Volatile.Write(ref running, 0);
            }
        })
        { IsBackground = true }).ToArray();

        var started = new List<Thread>();
        try
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            foreach (var thread in threads) { thread.Start(); started.Add(thread); }
            Thread.Sleep(100); // Reach steady state before the measured window.
            using var process = Process.GetCurrentProcess();
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
            if (!failures.IsEmpty) throw new AggregateException(failures);
            if (count == 0) throw new InvalidOperationException("No contexts were accepted.");
            return new Sample(count / elapsed, cpu / count, contentions / (double)count, bytes / (double)count);
        }
        finally
        {
            Volatile.Write(ref running, 0);
            var cleanup = Stopwatch.StartNew();
            try
            {
                foreach (var thread in started)
                {
                    var remaining = TimeSpan.FromSeconds(10) - cleanup.Elapsed;
                    if (remaining <= TimeSpan.Zero || !thread.Join(remaining))
                        throw new TimeoutException("Listener admission producer did not stop.");
                }
                // Accept every queued context; placeholders have no connection to close.
                while (pools.Sum(pool => pool.Count) != producers * depth)
                {
                    if (consumer.IsCompleted) consumer.GetAwaiter().GetResult();
                    if (cleanup.Elapsed > TimeSpan.FromSeconds(10))
                        throw new InvalidOperationException("Queued contexts were not accepted.");
                    Thread.Sleep(1);
                }
                if (!failures.IsEmpty) throw new AggregateException(failures);
            }
            finally
            {
                stop.Cancel();
                try { consumer.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                finally
                {
                    // A still-running failed worker must not race a disposed semaphore.
                    if (started.All(thread => !thread.IsAlive) && consumer.IsCompleted)
                        foreach (var gate in available) gate.Dispose();
                }
            }
        }
    }

    private static int ReadCount(string[] args, string name, int fallback, int maximum)
    {
        var value = ReadOption(args, name, fallback);
        if (!double.IsFinite(value) || value < 1 || value > maximum || value != Math.Truncate(value))
            throw new ArgumentOutOfRangeException(nameof(args), name + " must be a positive whole number within the supported range.");
        return (int)value;
    }

    private static double ReadOption(string[] args, string name, double fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return fallback;
        if (index + 1 >= args.Length) throw new ArgumentException(name + " requires a value.", nameof(args));
        return double.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private readonly record struct Sample(double RequestsPerSecond, double CpuMicroseconds, double Contentions, double Bytes);
}
