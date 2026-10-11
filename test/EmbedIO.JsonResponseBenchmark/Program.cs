using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Testing;

var mode = args[0];
var size = int.Parse(args[1]);
var iterations = int.Parse(args[2]);
if (mode is not ("legacy" or "current")) throw new ArgumentException("Unknown component path.");
var payload = new string('x', size);
var expected = Encoding.UTF8.GetBytes(EmbedIO.Serialization.Json.Serialize(payload));
ResponseSerializerCallback serialize = mode == "legacy" ? LegacyJson : ResponseSerializer.Json;
using var server = new TestWebServer();
server.WithModule(new ActionModule("/", HttpVerbs.Get, context => serialize(context, payload)));
using var stop = new CancellationTokenSource();
server.Start(stop.Token);
try
{
    for (var warm = 0; warm < 10; warm++) await Request();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocated = GC.GetTotalAllocatedBytes(true);
    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
    var elapsed = Stopwatch.StartNew();
    for (var iteration = 0; iteration < iterations; iteration++) await Request();
    elapsed.Stop();
    cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;
    allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode,
        size,
        iterations,
        allocated,
        bytesPerRequest = allocated / (double)iterations,
        cpuMilliseconds = cpu.TotalMilliseconds,
        elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
        scope = "In-memory HTTP pipeline; legacy callback reconstructed through public APIs; not network throughput",
    }));
}
finally { stop.Cancel(); }

async Task Request()
{
    var bytes = await server.Client.GetByteArrayAsync("/");
    if (!bytes.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("JSON bytes differ.");
}

// The previous HTTP JSON representation path, using unchanged public APIs on
// the same core so this comparison isolates representation/output work.
static Task LegacyJson(IHttpContext context, object? value)
{
    context.Response.ContentType = MimeType.Json;
    context.Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
    return ResponseSerializer.None(false)(context, EmbedIO.Serialization.Json.Serialize(value));
}
