using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EmbedIO;

internal static class ListenerWire
{
    internal static bool Run(string[] args)
    {
        var verify = args.Contains("--verify-listener-wire", StringComparer.Ordinal);
        if (!verify && !args.Contains("--listener-wire", StringComparer.Ordinal)) return false;
        var rows = new List<object>();
        var assembly = typeof(WebServer).Assembly;
        var chunk = assembly.GetType("EmbedIO.Net.Internal.ResponseStream", true)!
            .GetMethod("GetChunkSizeBytes", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<int, bool, byte[]>>();
        foreach (var size in new[] { 0, 15, 256, 16384 })
        {
            var final = size == 0;
            var expected = Encoding.UTF8.GetBytes($"{size:x}\r\n{(final ? "\r\n" : string.Empty)}");
            Measure("chunk-" + size, () =>
            {
                if (!chunk(size, final).AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Chunk bytes changed.");
            }, 40);
        }
        var charset = assembly.GetType("EmbedIO.Net.Internal.HeaderUtility", true)!
            .GetMethod("GetCharset")!.CreateDelegate<Func<string?, string?>>();
        foreach (var (name, value, expected, budget) in new[]
        {
            ("charset-none", "application/json", (string?)null, 0L),
            ("charset-utf8", "text/plain; charset=utf-8", "utf-8", 210L),
            ("charset-quoted", "text/plain; boundary=demo; charset=\"utf-8\"", "utf-8", 360L),
        })
            Measure(name, () => { if (charset(value) != expected) throw new InvalidOperationException("Charset selection changed."); }, budget);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            rows
        },
            new JsonSerializerOptions { WriteIndented = true }));
        return true;

        void Measure(string name, Action operation, long budget)
        {
            for (var i = 0; i < 10000; i++) operation();
            var samples = new List<(double Ns, double Bytes)>();
            for (var round = 0; round < 5; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 50000; i++) operation();
                watch.Stop();
                samples.Add((watch.Elapsed.TotalNanoseconds / 50000, (GC.GetAllocatedBytesForCurrentThread() - before) / 50000.0));
            }
            var bytes = samples.Select(r => r.Bytes).Order().ElementAt(2);
            rows.Add(new { name, medianNanoseconds = samples.Select(r => r.Ns).Order().ElementAt(2), allocatedBytes = bytes, maxBytes = budget });
            if (verify && bytes > budget + 1) throw new InvalidOperationException($"{name}: {bytes} B exceeds {budget} B.");
        }
    }
}
