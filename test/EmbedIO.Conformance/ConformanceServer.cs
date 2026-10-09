using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;

// Application surface shared by every conformance driver. Routes use only public
// EmbedIO APIs so the drivers exercise the engine as an application would.
internal sealed class ConformanceServer : IDisposable
{
    internal const int FileLength = 100_000;
    private static long _active;
    private static long _completed;
    private readonly List<WebServer> _servers = new();
    private readonly string _root;

    private ConformanceServer(string root) => _root = root;

    internal X509Certificate2? Certificate { get; private set; }

    internal static long Active => Interlocked.Read(ref _active);

    internal static byte[] FileBytes()
    {
        var bytes = new byte[FileLength];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + (i >> 8));
        return bytes;
    }

    internal static ConformanceServer Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "embedio-conformance-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "data.bin"), FileBytes());
        File.WriteAllText(Path.Combine(root, "text.txt"), string.Concat(Enumerable.Repeat("compressible text line\n", 2000)));
        return new ConformanceServer(root);
    }

    internal WebServer Add(string prefix, HttpListenerMode mode, bool tls)
    {
        var certificate = tls ? Certificate ??= HttpsSmoke.CreateCertificate() : null;
        var server = new WebServer(options =>
        {
            options.WithUrlPrefix(prefix).WithMode(mode);
            if (certificate != null) options.WithCertificate(certificate);
        });
        Configure(server);
        _servers.Add(server);
        return server;
    }

    private void Configure(WebServer server)
    {
        server
            .WithModule(new ActionModule("/plain", HttpVerbs.Any, context =>
                context.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding)))
            .WithModule(new ActionModule("/get-only", HttpVerbs.Get, context =>
                context.SendStringAsync("get", "text/plain", WebServer.Utf8NoBomEncoding)))
            .WithModule(new ActionModule("/echo", HttpVerbs.Any, EchoAsync))
            .WithModule(new ActionModule("/query", HttpVerbs.Query, EchoAsync))
            .WithModule(new ActionModule("/stream", HttpVerbs.Get, StreamAsync))
            .WithModule(new ActionModule("/slow", HttpVerbs.Any, SlowAsync))
            .WithModule(new ActionModule("/__stats", HttpVerbs.Get, StatsAsync))
            .WithStaticFolder("/files", _root, false);
    }

    // Echo reads the whole request body and reports what the application saw.
    private static async Task EchoAsync(IHttpContext context)
    {
        Interlocked.Increment(ref _active);
        try
        {
            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer, context.CancellationToken).ConfigureAwait(false);
            var body = buffer.ToArray();
            var response = context.Response;
            response.Headers["X-Method"] = context.Request.HttpMethod;
            response.Headers["X-Body-Length"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            response.Headers["X-Body-Sha256"] = Convert.ToHexString(SHA256.HashData(body));
            response.Headers["X-Target"] = context.Request.RawTarget;
            response.ContentType = "application/octet-stream";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
        }
    }

    // Unknown-length streamed response: /stream?n=bytes&chunk=size.
    private static async Task StreamAsync(IHttpContext context)
    {
        Interlocked.Increment(ref _active);
        try
        {
            var total = int.Parse(context.Request.QueryString["n"] ?? "4096", System.Globalization.CultureInfo.InvariantCulture);
            var chunk = int.Parse(context.Request.QueryString["chunk"] ?? "1000", System.Globalization.CultureInfo.InvariantCulture);
            if (total < 0 || total > 64 << 20 || chunk < 1 || chunk > 1 << 20) throw HttpException.BadRequest();
            context.Response.ContentType = "application/octet-stream";
            using var output = context.OpenResponseStream(false, false);
            var bytes = new byte[chunk];
            for (var sent = 0; sent < total; sent += chunk)
            {
                var count = Math.Min(chunk, total - sent);
                for (var i = 0; i < count; i++) bytes[i] = (byte)(sent + i);
                await output.WriteAsync(bytes.AsMemory(0, count), context.CancellationToken).ConfigureAwait(false);
                await output.FlushAsync(context.CancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
        }
    }

    // Delays before answering so drivers can cancel or reset an in-flight request.
    private static async Task SlowAsync(IHttpContext context)
    {
        Interlocked.Increment(ref _active);
        try
        {
            var delay = int.Parse(context.Request.QueryString["ms"] ?? "200", System.Globalization.CultureInfo.InvariantCulture);
            await Task.Delay(Math.Clamp(delay, 0, 10_000), context.CancellationToken).ConfigureAwait(false);
            await context.SendStringAsync("slow", "text/plain", WebServer.Utf8NoBomEncoding).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
        }
    }

    // Resource snapshot after a full blocking collection. Not a production endpoint.
    private static Task StatsAsync(IHttpContext context) => context.SendStringAsync(Snapshot(), "application/json", WebServer.Utf8NoBomEncoding);

    internal static string Snapshot()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        using var process = Process.GetCurrentProcess();
        return JsonSerializer.Serialize(new
        {
            managedBytes = GC.GetTotalMemory(false),
            heapSizeBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
            handles = process.HandleCount,
            threads = process.Threads.Count,
            workingSet = process.WorkingSet64,
            activeHandlers = Interlocked.Read(ref _active),
            completedHandlers = Interlocked.Read(ref _completed),
            threadPoolPending = ThreadPool.PendingWorkItemCount,
        });
    }

    public void Dispose()
    {
        foreach (var server in _servers) server.Dispose();
        Certificate?.Dispose();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string Describe(IReadOnlyList<(string Name, string Url, HttpListenerMode Mode)> endpoints) =>
        JsonSerializer.Serialize(endpoints.Select(e => new { e.Name, e.Url, Mode = e.Mode.ToString() }));

    internal static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
