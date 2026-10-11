using System.Diagnostics;
using System.Net;
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
    private static long _capsuleAfterFinMessages;
    private static long _capsuleAfterFinByteSum;
    private static long _drainsStarted;
    private static long _drainsCompleted;
    private static string? _drainFailure;
    private static long _drainMilliseconds;
    private readonly List<WebServer> _servers = new();
    private readonly Dictionary<string, WebServer> _byEndpoint = new(StringComparer.Ordinal);
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
        _byEndpoint[mode is HttpListenerMode.EmbedIOHttp3 or HttpListenerMode.EmbedIOCombined ? "h3" : tls ? "https" : "http"] = server;
        return server;
    }

    private void Configure(WebServer server)
    {
        server
            .WithModule(new ActionModule("/plain", HttpVerbs.Any, context =>
                context.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding)))
            .WithModule(new ActionModule("/get-only", HttpVerbs.Get, context =>
                context.SendStringAsync("get", "text/plain", WebServer.Utf8NoBomEncoding)))
            .WithModule(new ActionModule("/capsule", HttpVerbs.Any, CapsuleAsync))
            .WithModule(new ActionModule("/sections", HttpVerbs.Get, SectionsAsync))
            .WithModule(new ActionModule("/lifecycle", HttpVerbs.Get, LifecycleAsync))
            .WithModule(new ActionModule("/__drain", HttpVerbs.Post, StartDrainAsync))
            .WithModule(new ActionModule("/echo", HttpVerbs.Any, EchoAsync))
            .WithModule(new ActionModule("/query", HttpVerbs.Query, EchoAsync))
            .WithModule(new ActionModule("/stream", HttpVerbs.Get, StreamAsync))
            .WithModule(new ActionModule("/slow", HttpVerbs.Any, SlowAsync))
            .WithModule(new ActionModule("/__stats", HttpVerbs.Get, StatsAsync))
            .WithModule(new ActionModule("/probe/status-103", HttpVerbs.Any, Status103Async))
            .WithModule(new ActionModule("/probe/reject", HttpVerbs.Any, RejectAsync))
            .WithModule(new ActionModule("/probe/fields", HttpVerbs.Any, FieldsAsync))
            .WithStaticFolder("/files", _root, false);
    }

    private static async Task SectionsAsync(IHttpContext context)
    {
        if (context.Response is not IHttpResponseSections sections)
            throw new HttpException(HttpStatusCode.NotImplemented, "This backend has no response-section capability.");
        var size = int.Parse(context.Request.QueryString["size"] ?? "3", System.Globalization.CultureInfo.InvariantCulture);
        if (size is not (0 or 3 or 196608)) throw HttpException.BadRequest("Unsupported test payload size.");
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        Interlocked.Increment(ref _active);
        try
        {
            await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</one>; rel=preload" }, context.CancellationToken);
            await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</two>; rel=preload" }, context.CancellationToken);
            sections.DeclareTrailers("Content-Digest", "X-Section-End");
            if (context.Request.ProtocolVersion.Major >= 2 && context.Request.QueryString["fixed"] == "1")
                context.Response.ContentLength64 = bytes.Length;
            for (var offset = 0; offset < bytes.Length; offset += 16384)
                await context.Response.OutputStream.WriteAsync(bytes, offset, Math.Min(16384, bytes.Length - offset), context.CancellationToken);
            var digest = Convert.ToBase64String(SHA256.HashData(bytes));
            sections.SetTrailers(new WebHeaderCollection { ["Content-Digest"] = "sha-256=:" + digest + ":", ["X-Section-End"] = "finished" });
        }
        finally { Interlocked.Decrement(ref _active); Interlocked.Increment(ref _completed); }
    }

    // Lifecycle campaign resource: interim sections, a paced body and optional
    // trailers carrying the body digest. /lifecycle?interim=0..3&size=N&chunk=N&delay=ms&trailers=0|1&fixed=0|1
    private static async Task LifecycleAsync(IHttpContext context)
    {
        if (context.Response is not IHttpResponseSections sections)
            throw new HttpException(HttpStatusCode.NotImplemented, "This backend has no response-section capability.");
        int Query(string name, int fallback, int maximum)
        {
            var value = int.Parse(context.Request.QueryString[name] ?? fallback.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
            return value < 0 || value > maximum ? throw HttpException.BadRequest("Unsupported lifecycle parameter " + name + ".") : value;
        }
        var interim = Query("interim", 0, 3);
        var size = Query("size", 0, 4 << 20);
        var chunk = Math.Max(1, Query("chunk", 16384, 1 << 20));
        var delay = Query("delay", 0, 1000);
        var trailers = Query("trailers", 0, 1) == 1;
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        Interlocked.Increment(ref _active);
        try
        {
            for (var i = 0; i < interim; i++)
                await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = $"</i{i}>; rel=preload" }, context.CancellationToken);
            if (trailers) sections.DeclareTrailers("x-lifecycle-sha256", "x-lifecycle-end");
            if (Query("fixed", 0, 1) == 1 && (!trailers || context.Request.ProtocolVersion.Major >= 2)) context.Response.ContentLength64 = size;
            context.Response.ContentType = "application/octet-stream";
            for (var offset = 0; offset < bytes.Length; offset += chunk)
            {
                if (offset != 0 && delay != 0) await Task.Delay(delay, context.CancellationToken);
                await context.Response.OutputStream.WriteAsync(bytes.AsMemory(offset, Math.Min(chunk, bytes.Length - offset)), context.CancellationToken);
                await context.Response.OutputStream.FlushAsync(context.CancellationToken);
            }
            if (trailers)
                sections.SetTrailers(new WebHeaderCollection { ["x-lifecycle-sha256"] = Convert.ToHexString(SHA256.HashData(bytes)), ["x-lifecycle-end"] = "done" });
        }
        finally { Interlocked.Decrement(ref _active); Interlocked.Increment(ref _completed); }
    }

    // Starts a graceful drain of one named endpoint (http, https or h3) without awaiting
    // it inside the request. POST /__drain?endpoint=NAME&ms=DEADLINE. Outcomes appear in
    // /__stats. Drain stops that endpoint, so a campaign uses it as its last phase.
    private Task StartDrainAsync(IHttpContext context)
    {
        var name = context.Request.QueryString["endpoint"] ?? "";
        if (!_byEndpoint.TryGetValue(name, out var target)) throw HttpException.BadRequest("Unknown endpoint.");
        var deadline = TimeSpan.FromMilliseconds(int.Parse(context.Request.QueryString["ms"] ?? "10000", System.Globalization.CultureInfo.InvariantCulture));
        Interlocked.Increment(ref _drainsStarted);
        _ = Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await target.DrainAsync(deadline).ConfigureAwait(false);
                Interlocked.Increment(ref _drainsCompleted);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Volatile.Write(ref _drainFailure, error.GetType().Name + ": " + error.Message);
            }
            finally { Interlocked.Exchange(ref _drainMilliseconds, watch.ElapsedMilliseconds); }
        });
        context.Response.StatusCode = 202;
        return context.SendStringAsync("draining " + name, "text/plain", WebServer.Utf8NoBomEncoding);
    }

    // A test-only extension carrier. Type 0 echoes opaque HTTP Datagram payloads;
    // unknown types are skipped. It never forwards UDP or changes production defaults.
    private static async Task CapsuleAsync(IHttpContext context)
    {
        if (context is not IHttpTunnelContext capability)
            throw new HttpException(HttpStatusCode.NotImplemented, "This backend has no optional tunnel capability.");
        Interlocked.Increment(ref _active);
        try
        {
            var tunnel = await capability.AcceptTunnelAsync("example-tunnel", true, context.CancellationToken).ConfigureAwait(false);
            var channel = tunnel.Capsules ?? throw new InvalidOperationException("Missing accepted capsule channel.");
            var scratch = new byte[4096];
            var halfClose = context.Request.QueryString["half-close"] == "true";
            var outputComplete = false;
            while (await channel.ReadHeaderAsync(context.CancellationToken).ConfigureAwait(false) is { } header)
            {
                if (header.Type != 0)
                {
                    await channel.SkipPayloadAsync(context.CancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (header.Length > 16384)
                {
                    await channel.SkipPayloadAsync(context.CancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!outputComplete)
                    await channel.WriteHeaderAsync(0, header.Length, context.CancellationToken).ConfigureAwait(false);
                var remaining = header.Length;
                while (remaining != 0)
                {
                    var count = await channel.ReadPayloadAsync(scratch, 0, (int)Math.Min(remaining, scratch.Length), context.CancellationToken).ConfigureAwait(false);
                    if (!outputComplete) await channel.WritePayloadAsync(scratch, 0, count, context.CancellationToken).ConfigureAwait(false);
                    if (outputComplete)
                        for (var i = 0; i < count; i++) Interlocked.Add(ref _capsuleAfterFinByteSum, scratch[i]);
                    remaining -= count;
                }
                if (outputComplete) Interlocked.Increment(ref _capsuleAfterFinMessages);
                if (halfClose && !outputComplete)
                {
                    await tunnel.CompleteOutputAsync(context.CancellationToken).ConfigureAwait(false);
                    outputComplete = true;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
        }
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

    // Applicability probes. Each uses only the public response API an application
    // would reach for, so the observed wire behavior is what applications get.

    // An application attempting Early Hints through the only public status setter.
    private static async Task Status103Async(IHttpContext context)
    {
        context.Response.StatusCode = 103;
        context.Response.Headers["Link"] = "</files/text.txt>; rel=preload";
        await context.Response.OutputStream.FlushAsync(context.CancellationToken).ConfigureAwait(false);
    }

    // Rejects without reading the request content (for Expect: 100-continue).
    private static Task RejectAsync(IHttpContext context)
    {
        context.Response.StatusCode = 413;
        return context.SendStringAsync("rejected", "text/plain", WebServer.Utf8NoBomEncoding);
    }

    // Reads the content, then reports the request field names the application can see.
    private static async Task FieldsAsync(IHttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(buffer, context.CancellationToken).ConfigureAwait(false);
        var names = context.Request.Headers.AllKeys.OfType<string>().Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal);
        await context.SendStringAsync($"body={buffer.Length};fields={string.Join(",", names)}", "text/plain", WebServer.Utf8NoBomEncoding).ConfigureAwait(false);
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
            capsuleAfterFinMessages = Interlocked.Read(ref _capsuleAfterFinMessages),
            capsuleAfterFinByteSum = Interlocked.Read(ref _capsuleAfterFinByteSum),
            threadPoolPending = ThreadPool.PendingWorkItemCount,
            drainsStarted = Interlocked.Read(ref _drainsStarted),
            drainsCompleted = Interlocked.Read(ref _drainsCompleted),
            drainFailure = Volatile.Read(ref _drainFailure),
            drainMilliseconds = Interlocked.Read(ref _drainMilliseconds),
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
