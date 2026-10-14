using System.Collections.Specialized;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.Files;
using EmbedIO.Net;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;

// --static-file-timing measures FileModule's request handling in process, without a
// listener: a cached small file (GET and HEAD), a single byte range of it, a precompressed
// variant and an uncached file streamed from disk, each through the module's public
// HandleRequestAsync with a minimal context. Run the same build against a baseline and a
// candidate EmbedIO.dll (copy the output and replace only the core) and compare ns/op and
// B/op; every operation validates status and body. Allocations are counted for the whole
// process because file reads complete on I/O threads.
internal static class StaticFileTiming
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--static-file-timing", StringComparer.Ordinal)) return false;
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
        var assembly = typeof(WebServer).Assembly;
        Console.WriteLine($"core: {assembly.Location}; sha256={Hash(assembly.Location)}");
        var root = Directory.CreateTempSubdirectory("embedio-static-timing-");
        try
        {
            var small = Pattern(1024, 7);
            var large = Pattern(256 * 1024, 11);
            var script = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"console.log('line {i % 37}');\n")));
            var variant = Brotli(script);
            var modified = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            Write(root.FullName, "small.txt", small, modified);
            Write(root.FullName, "large.bin", large, modified);
            Write(root.FullName, "app.js", script, modified);
            Write(root.FullName, "app.js.br", variant, modified.AddMinutes(1));
            using var cache = new FileCache();
            using var module = new FileModule("/", new FileSystemProvider(root.FullName, isImmutable: true))
            {
                Cache = cache,
                PrecompressedVariants = PrecompressedVariants.All,
            };
            module.Start(CancellationToken.None);
            Measure("static-cached-small-get-1k", Workload(module, "/small.txt", HttpVerbs.Get, null, 200, small), 100000);
            Measure("static-cached-small-head-1k", Workload(module, "/small.txt", HttpVerbs.Head, null, 200, Array.Empty<byte>(), small.Length), 100000);
            Measure("static-cached-range-100", Workload(module, "/small.txt", HttpVerbs.Get, ("Range", "bytes=100-199"), 206, small.AsSpan(100, 100).ToArray()), 100000);
            Measure("static-precompressed-br-" + variant.Length, Workload(module, "/app.js", HttpVerbs.Get, ("Accept-Encoding", "br"), 200, variant), 100000);
            Measure("static-uncached-stream-256k", Workload(module, "/large.bin", HttpVerbs.Get, null, 200, large), 2000);
            // The first workload once more, after the others, as an ordering control: a difference
            // between the two readings is warm-up or tiering state, not the module.
            Measure("static-cached-small-get-1k-again", Workload(module, "/small.txt", HttpVerbs.Get, null, 200, small), 100000);
            return true;
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static byte[] Pattern(int length, int step)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)('a' + (i * step) % 26);
        return bytes;
    }

    private static byte[] Brotli(byte[] content)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, true)) brotli.Write(content, 0, content.Length);
        return output.ToArray();
    }

    private static void Write(string root, string name, byte[] content, DateTime modifiedUtc)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    // One request through the module. The context is reused and reset per operation so the
    // measurement covers FileModule rather than request construction.
    private static Func<int> Workload(FileModule module, string path, HttpVerbs verb, (string Name, string Value)? header, int expectedStatus, byte[] expectedBody, int expectedLength = -1)
    {
        var context = new BenchmarkContext(new BenchmarkRequest(path, verb, header), path);
        var expected = expectedLength < 0 ? expectedBody.Length : expectedLength;
        return () =>
        {
            context.Reset();
            module.HandleRequestAsync(context).GetAwaiter().GetResult();
            var response = context.BenchmarkResponse;
            if (response.StatusCode != expectedStatus) throw new InvalidOperationException($"{path}: status {response.StatusCode} instead of {expectedStatus}.");
            var body = response.Output;
            if (!body.GetBuffer().AsSpan(0, (int)body.Length).SequenceEqual(expectedBody)) throw new InvalidOperationException($"{path}: unexpected body of {body.Length} bytes.");
            return verb == HttpVerbs.Head ? (int)response.ContentLength64 : (int)body.Length;
        };
    }

    // Warms the operation for half a second so tiered compilation and dynamic PGO have
    // settled, then reports the median of five rounds; allocations are process-wide.
    private static void Measure(string name, Func<int> operation, int iterations)
    {
        var expected = operation();
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(500)) _ = operation();
        var samples = new List<(double Nanoseconds, double Bytes)>();
        var checksum = 0L;
        for (var round = 0; round < 5; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(true);
            var begin = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++) checksum += operation();
            var elapsed = Stopwatch.GetElapsedTime(begin);
            var allocated = GC.GetTotalAllocatedBytes(true) - before;
            samples.Add((elapsed.TotalNanoseconds / iterations, (double)allocated / iterations));
        }

        if (checksum != (long)expected * iterations * 5) throw new InvalidOperationException($"{name} returned an unexpected checksum.");
        var ns = samples.Select(sample => sample.Nanoseconds).Order().ElementAt(2);
        var bytes = samples.Select(sample => sample.Bytes).Order().ElementAt(2);
        Console.WriteLine($"{name}: {ns:F1} ns/op; {bytes:F1} B/op; checksum={checksum}");
    }

    private sealed class BenchmarkRequest : IHttpRequest
    {
        internal BenchmarkRequest(string path, HttpVerbs verb, (string Name, string Value)? header)
        {
            Headers = new NameValueCollection();
            if (header is { } field) Headers[field.Name] = field.Value;
            HttpVerb = verb;
            HttpMethod = verb == HttpVerbs.Head ? "HEAD" : "GET";
            RawTarget = path;
            Url = new Uri("http://localhost" + path);
        }

        public ICookieCollection Cookies { get; } = new CookieList();
        public Version ProtocolVersion => HttpVersion.Version11;
        public NameValueCollection Headers { get; }
        public bool KeepAlive => true;
        public string RawTarget { get; }
        public NameValueCollection QueryString { get; } = new();
        public string HttpMethod { get; }
        public HttpVerbs HttpVerb { get; }
        public Uri Url { get; }
        public bool HasEntityBody => false;
        public Stream InputStream => Stream.Null;
        public Encoding ContentEncoding => Encoding.UTF8;
        public IPEndPoint RemoteEndPoint { get; } = new(IPAddress.Loopback, 9999);
        public bool IsLocal => true;
        public bool IsSecureConnection => false;
        public string? UserAgent => null;
        public bool IsWebSocketRequest => false;
        public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 8080);
        public string? ContentType => null;
        public long ContentLength64 => 0;
        public bool IsAuthenticated => false;
        public Uri? UrlReferrer => null;
    }

    private sealed class BenchmarkResponse : IHttpResponse
    {
        public WebHeaderCollection Headers { get; } = new();
        public int StatusCode { get; set; } = 200;
        public long ContentLength64 { get; set; }
        public string? ContentType { get; set; }
        public MemoryStream Output { get; } = new();
        public Stream OutputStream => Output;
        public ICookieCollection Cookies { get; } = new CookieList();
        public Encoding? ContentEncoding { get; set; } = WebServer.DefaultEncoding;
        public bool KeepAlive { get; set; }
        public bool SendChunked { get; set; }
        public Version ProtocolVersion => HttpVersion.Version11;
        public string? StatusDescription { get; set; }
        public void SetCookie(Cookie cookie) => Cookies.Add(cookie);
        public void Close() { }

        internal void Reset()
        {
            Headers.Clear();
            StatusCode = 200;
            ContentLength64 = 0;
            ContentType = null;
            ContentEncoding = WebServer.DefaultEncoding;
            SendChunked = false;
            StatusDescription = null;
            Output.SetLength(0);
        }
    }

    // The members FileModule and the module base read; the route names the requested path.
    private sealed class BenchmarkContext : IHttpContextImpl
    {
        internal BenchmarkContext(BenchmarkRequest request, string path)
        {
            Request = request;
            Route = RouteMatch.UnsafeFromRoot(path);
        }

        internal BenchmarkResponse BenchmarkResponse { get; } = new();
        public string Id => "static-file-timing";
        public CancellationToken CancellationToken { get; set; }
        public IPEndPoint LocalEndPoint => Request.LocalEndPoint;
        public IPEndPoint RemoteEndPoint => Request.RemoteEndPoint;
        public IHttpRequest Request { get; }
        public RouteMatch Route { get; set; }
        public string RequestedPath => Route.SubPath ?? string.Empty;
        public IHttpResponse Response => BenchmarkResponse;
        public IPrincipal User { get; set; } = Auth.NoUser;
        public ISessionProxy Session { get; set; } = SessionProxy.None;
        public bool SupportCompressedRequests { get; set; }
        public IDictionary<object, object> Items { get; } = new Dictionary<object, object>();
        public long Age => 0;
        public bool IsHandled { get; private set; }
        public MimeTypeProviderStack MimeTypeProviders { get; } = new();
        public void SetHandled() => IsHandled = true;
        public void OnClose(Action<IHttpContext> callback) { }
        public void Close() { }
        public Task<IWebSocketContext> AcceptWebSocketAsync(IEnumerable<string> requestedProtocols, string acceptedProtocol, int receiveBufferSize, TimeSpan keepAliveInterval, CancellationToken cancellationToken)
            => throw new NotSupportedException("The benchmark context does not accept WebSockets.");
        public string? GetMimeType(string extension) => MimeTypeProviders.GetMimeType(extension);
        public bool TryDetermineCompression(string mimeType, out bool preferCompression) => MimeTypeProviders.TryDetermineCompression(mimeType, out preferCompression);

        internal void Reset()
        {
            IsHandled = false;
            Items.Clear();
            BenchmarkResponse.Reset();
        }
    }
}
