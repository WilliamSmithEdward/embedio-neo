using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.Utilities;

if (EngineLoad.Run(args) || EngineParser.Run(args) || BenchmarkHost.Run(args) || ColdStart.Run(args) || ListenerQueue.Run(args) || ListenerHttp.Run(args) || ListenerAllocations.Run(args) || ListenerWire.Run(args)) return;

var verifyAllocations = args.Contains("--verify-allocations", StringComparer.Ordinal);
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
var route = RouteMatcher.Parse("/items/{id}/{name?}", false);
Measure("route-parse-cached", () => RouteMatcher.Parse("/items/{id}/{name?}", false), maxBytes: 64);
Measure("route-two-parameters", () => route.Match("/items/42/hello%20world"), maxBytes: 900);
var literal = RouteMatcher.Parse("/items", false);
Measure("route-literal", () => literal.Match("/items"), maxBytes: 360);
Measure("normalize-ordinary", () => UrlPath.Normalize("/api/items/42", false));
Measure("normalize-slashes", () => UrlPath.Normalize("//api///items//42///", false));
var read = ((((typeof(WebServer).Assembly.GetType("EmbedIO.Internal.StreamExtensions", true)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetMethod("ReadBytesAsync", BindingFlags.Static | BindingFlags.NonPublic)) ?? throw new System.InvalidOperationException("Expected a non-null test value."))
    .CreateDelegate<Func<Stream, int, int, Task<byte[]>>>();
using var small = new MemoryStream(new byte[2]);
Measure("websocket-read-2", () => { small.Position = 0; return read(small, 2, 4096).GetAwaiter().GetResult(); }, maxBytes: 256);
using var large = new MemoryStream(new byte[16384]);
Measure("websocket-read-16k", () => { large.Position = 0; return read(large, 16384, 4096).GetAwaiter().GetResult(); }, maxBytes: 34000);
var frameType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true);
var finType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.Fin", true);
var opcodeType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Opcode", true);
foreach (var size in new[] { 0, 125, 126, 65536 })
{
    var frame = Activator.CreateInstance(((frameType) ?? throw new System.InvalidOperationException("Expected a non-null test value.")), BindingFlags.Instance | BindingFlags.NonPublic, null,
        new[] { Enum.Parse(((finType) ?? throw new System.InvalidOperationException("Expected a non-null test value.")), "Final"), Enum.Parse(((opcodeType) ?? throw new System.InvalidOperationException("Expected a non-null test value.")), "Binary"), (object)new byte[size], false }, null);
    var serialize = ((frameType.GetMethod("ToArray")) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).CreateDelegate<Func<byte[]>>(frame);
    Measure("websocket-write-" + size, () => serialize(), size < 1000 ? 100000 : 10000, size + 128);
}
var writeLog = ((typeof(EmbedIO.Diagnostics.Log).GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Static)) ?? throw new System.InvalidOperationException("Expected a non-null test value."))
    .CreateDelegate<Action<TraceEventType, string, string>>();
EmbedIO.Diagnostics.Log.Source.Switch.Level = SourceLevels.Off;
Measure("diagnostic-disabled", () => { writeLog(TraceEventType.Verbose, "benchmark", "ordinary message"); return null; }, maxBytes: 0);


var negotiation = new QValueList(true, "gzip, deflate, identity;q=0.5");
Measure("encoding-negotiate", () => { negotiation.TryNegotiateContentEncoding(true, out _, out var method); return method; }, maxBytes: 64);
Measure("qvalues-plain", () => new QValueList(true, "gzip, deflate, br"), maxBytes: 480);
Measure("qvalues-weighted", () => new QValueList(true, "gzip;q=0.9, deflate;q=0.8, identity;q=0.5"), maxBytes: 512);
var resolved = route.Match("/items/42/name");
Measure("route-key-lookup", () => { if (!((resolved) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).ContainsKey("name")) throw new Exception(); return null; }, maxBytes: 0);
var headers = new System.Collections.Specialized.NameValueCollection
{
    ["Connection"] = "keep-alive, Upgrade, custom-1, custom-2, custom-3"
};
Measure("header-token-early", () => { if (!headers.Contains("Connection", "keep-alive", StringComparison.OrdinalIgnoreCase)) throw new Exception(); return null; }, maxBytes: 64);
Measure("header-token-late", () => { if (!headers.Contains("Connection", "custom-3", StringComparison.OrdinalIgnoreCase)) throw new Exception(); return null; }, maxBytes: 400);
Measure("token-validation", () => Validate.Rfc2616Token("token", "vnd.example.resource+json"), maxBytes: 0);
foreach (var size in new[] { 126, 65536 })
{
    var frame = Activator.CreateInstance(((frameType) ?? throw new System.InvalidOperationException("Expected a non-null test value.")), BindingFlags.Instance | BindingFlags.NonPublic, null,
        new[] { Enum.Parse(((finType) ?? throw new System.InvalidOperationException("Expected a non-null test value.")), "Final"), Enum.Parse(((opcodeType) ?? throw new System.InvalidOperationException("Expected a non-null test value.")), "Binary"), (object)new byte[size], false }, null);
    var readLength = ((((frameType.GetProperty("FullPayloadLength", BindingFlags.Instance | BindingFlags.NonPublic)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetMethod) ?? throw new System.InvalidOperationException("Expected a non-null test value."))
        .CreateDelegate<Func<ulong>>(frame);
    Measure("websocket-length-" + size, () => { if (readLength() != (ulong)size) throw new Exception(); return null; }, maxBytes: 0);
}
var payloadType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.PayloadData", true);
var appendClose = ((((payloadType) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetMethod("Append", BindingFlags.Static | BindingFlags.NonPublic)) ?? throw new System.InvalidOperationException("Expected a non-null test value."))
    .CreateDelegate<Func<ushort, string?, byte[]>>();
Measure("websocket-close-reason", () => appendClose(1000, "normal closure"), maxBytes: 64);
using var partial = new MemoryStream(new byte[4096]);
Measure("websocket-read-truncated-16k", () => { partial.Position = 0; return read(partial, 16384, 4096).GetAwaiter().GetResult(); }, maxBytes: 9000);
using var uneven = new MemoryStream(new byte[12000]);
Measure("websocket-read-12k", () => { uneven.Position = 0; return read(uneven, 12000, 4096).GetAwaiter().GetResult(); }, maxBytes: 46000);
var criterionType = typeof(EmbedIO.Security.IPBanningRequestsCriterion);
using var criterion = (EmbedIO.Security.IPBanningRequestsCriterion)((Activator.CreateInstance(criterionType,
    BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { int.MaxValue }, null)) ?? throw new System.InvalidOperationException("Expected a non-null test value."));
var histories = (System.Collections.IDictionary)((((criterionType.GetField("Requests", BindingFlags.Static | BindingFlags.NonPublic)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetValue(null)) ?? throw new System.InvalidOperationException("Expected a non-null test value."));
var historyType = ((((histories.GetType()) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetGenericArguments()) ?? throw new System.InvalidOperationException("Expected a non-null test value."))[1];
var history = Activator.CreateInstance(historyType);
var addTime = ((historyType.GetMethod("Add")) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).CreateDelegate<Action<long>>(history);
var address = System.Net.IPAddress.Parse("192.0.2.197");
histories[address] = history;
for (var i = 0; i < 5000; i++) addTime(DateTime.Now.AddSeconds(-2).Ticks);
Measure("ip-history-5000", () =>
{
    try { if (criterion.ValidateIPAddress(address).GetAwaiter().GetResult()) throw new Exception(); return null; }
    finally
    {
        if (history is System.Collections.Concurrent.ConcurrentBag<long> bag) bag.TryTake(out _);
        else { var list = (IList<long>)((history) ?? throw new System.InvalidOperationException("Expected a non-null test value.")); ((list) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).RemoveAt(list.Count - 1); }
    }
}, iterations: 5000, maxBytes: 0);

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
