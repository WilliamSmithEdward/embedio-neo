using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.Utilities;

var verifyAllocations = args.Contains("--verify-allocations", StringComparer.Ordinal);
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
var route = RouteMatcher.Parse("/items/{id}/{name?}", false);
Measure("route-parse-cached", () => RouteMatcher.Parse("/items/{id}/{name?}", false), maxBytes: 64);
Measure("route-two-parameters", () => route.Match("/items/42/hello%20world"), maxBytes: 900);
var literal = RouteMatcher.Parse("/items", false);
Measure("route-literal", () => literal.Match("/items"), maxBytes: 360);
Measure("normalize-ordinary", () => UrlPath.Normalize("/api/items/42", false));
Measure("normalize-slashes", () => UrlPath.Normalize("//api///items//42///", false));
var read = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.StreamExtensions", true)!.GetMethod("ReadBytesAsync", BindingFlags.Static | BindingFlags.NonPublic)!
    .CreateDelegate<Func<Stream, int, int, Task<byte[]>>>();
using var small = new MemoryStream(new byte[2]);
Measure("websocket-read-2", () => { small.Position = 0; return read(small, 2, 4096).GetAwaiter().GetResult(); }, maxBytes: 256);
using var large = new MemoryStream(new byte[16384]);
Measure("websocket-read-16k", () => { large.Position = 0; return read(large, 16384, 4096).GetAwaiter().GetResult(); });
var frameType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true)!;
var finType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.Fin", true)!;
var opcodeType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Opcode", true)!;
foreach (var size in new[] { 0, 125, 126, 65536 })
{
    var frame = Activator.CreateInstance(frameType, BindingFlags.Instance | BindingFlags.NonPublic, null,
        new[] { Enum.Parse(finType, "Final"), Enum.Parse(opcodeType, "Binary"), (object)new byte[size], false }, null)!;
    var serialize = frameType.GetMethod("ToArray")!.CreateDelegate<Func<byte[]>>(frame);
    Measure("websocket-write-" + size, () => serialize(), size < 1000 ? 100000 : 10000, size + 128);
}
var writeLog = typeof(EmbedIO.Diagnostics.Log).GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Static)!
    .CreateDelegate<Action<TraceEventType, string, string>>();
EmbedIO.Diagnostics.Log.Source.Switch.Level = SourceLevels.Off;
Measure("diagnostic-disabled", () => { writeLog(TraceEventType.Verbose, "benchmark", "ordinary message"); return null; }, maxBytes: 0);

void Measure(string name, Func<object?> operation, int iterations = 100000, long maxBytes = long.MaxValue)
{
    for (var i = 0; i < 20000; i++) GC.KeepAlive(operation());
    var rows = new List<(double Ns, double Bytes)>();
    for (var round = 0; round < 7; round++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) GC.KeepAlive(operation());
        var elapsed = Stopwatch.GetElapsedTime(start);
        rows.Add((elapsed.TotalNanoseconds / iterations, (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations));
    }
    rows.Sort((a, b) => a.Ns.CompareTo(b.Ns));
    Console.WriteLine($"{name}: {rows[3].Ns:F1} ns/op, {rows[3].Bytes:F0} B/op");
    if (verifyAllocations && rows[3].Bytes > maxBytes)
        throw new InvalidOperationException($"{name} allocated {rows[3].Bytes:F0} B/op; budget is {maxBytes} B/op.");
}
