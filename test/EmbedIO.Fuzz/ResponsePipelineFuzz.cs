using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Files;

// A known immutable file is the body oracle. Tags are learned as opaque validators,
// never recomputed using the production implementation. Three gated clients really overlap.
internal static class ResponsePipelineFuzz
{
    internal static Task Http1Async(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 1, token);
    internal static Task Http2Async(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 2, token);
    internal static Task Http3Async(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 3, token);

    private static async Task RunAsync(StateMachineTrace trace, int protocol, CancellationToken token, string? fault = null)
    {
        if (protocol == 3 && !QuicListener.IsSupported)
            throw new PlatformNotSupportedException("Response HTTP/3 requires working QUIC support.");
        var root = Directory.CreateTempSubdirectory("embedio-response-fuzz-").FullName;
        try
        {
            var sizes = new[] { 0, 1, 127, 128, 4096, 8192, 65535, 65536, 65537 };
            var data = Enumerable.Range(0, sizes[(int)((uint)trace.Seed % (uint)sizes.Length)])
                .Select(index => (byte)('a' + (index & 15))).ToArray();
            if ((trace.Seed & 2) != 0) new Random(trace.Seed).NextBytes(data);
            var path = Path.Combine(root, "item.txt");
            var fileBytes = (byte[])data.Clone();
            if (fault == "body") fileBytes[0] ^= 1;
            await File.WriteAllBytesAsync(path, fileBytes, token).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            using var certificate = protocol == 3 ? ConnectionIsolationFuzz.Certificate() : null;
            var url = ConnectionIsolationFuzz.Prefix(protocol == 3);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var cache = new FileCache();
            using var server = new WebServer(options =>
            {
                options.WithUrlPrefix(url).WithMode(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO);
                if (certificate != null) options.WithCertificate(certificate);
            }).PreferCompressionFor("text/plain");
            var requests = new ConcurrentDictionary<string, Request>(StringComparer.Ordinal);
            var faults = new ConcurrentQueue<Exception>();
            var all = new List<Request>();
            var pending = new Request?[3];
            var tags = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var representationLengths = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var clients = Enumerable.Range(0, 3).Select(_ => Client(protocol, certificate)).ToArray();
            var sequence = 0;
            server.WithModule(new Gate(async context =>
            {
                var id = context.Request.Headers["X-Fuzz-Request"];
                if (id == null || !requests.TryGetValue(id, out var request))
                    throw new StateMachineFailure("response.dispatch-identity", "Unknown request was dispatched.");
                context.OnClose(_ => { Interlocked.Increment(ref request.CloseCount); request.Closed.TrySetResult(); });
                try
                {
                    Require(context.Request.Cookies["client"]?.Value == request.Actor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "response.request-cookie-isolation", request.Id);
                    request.RemotePort = context.RemoteEndPoint.Port;
                    request.Entered.TrySetResult();
                    await request.Release.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                    if (request.Canceled) { context.SetHandled(); return; }
                    context.Response.Headers.Add("Set-Cookie", "raw=" + request.Id + "; Path=/");
                    context.Response.SetCookie(new Cookie("actor", fault == "cookie" ? "wrong" : request.Actor.ToString(System.Globalization.CultureInfo.InvariantCulture), "/"));
                }
                catch (Exception error)
                {
                    if (error is StateMachineFailure || !(request.Canceled && error is OperationCanceledException)) faults.Enqueue(error);
                    request.Entered.TrySetException(error);
                    throw;
                }
                finally { request.GateExited.TrySetResult(); }
            }));
            server.WithModule(new ActionModule("/trailers", HttpVerbs.Get, async context =>
            {
                var id = context.Request.Headers["X-Fuzz-Request"] ?? throw new InvalidOperationException("Missing request id.");
                var request = requests[id];
                var sections = context.Response as IHttpResponseSections ?? throw new StateMachineFailure("response.trailer-capability", id);
                sections.DeclareTrailers("x-fuzz-end");
                var snapshot = new WebHeaderCollection { ["x-fuzz-end"] = fault == "trailer" ? "wrong" : request.Id };
                sections.SetTrailers(snapshot);
                snapshot["x-fuzz-end"] = "mutated-after-copy";
                context.Response.ContentType = "text/plain";
                if ((request.Value & 1) != 0) context.Response.Headers["X-Large"] = new string('x', 4096);
                await using var output = context.OpenResponseStream(false, true);
                var body = request.Body;
                var split = request.Value >= 32 ? 1 + request.Value % body.Length : request.Value % (body.Length + 1);
                await output.WriteAsync(body.AsMemory(0, split), context.CancellationToken).ConfigureAwait(false);
                if (request.Value >= 32)
                {
                    await output.FlushAsync(context.CancellationToken).ConfigureAwait(false);
                    request.PartialWritten.TrySetResult();
                    await request.BodyRelease.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                    if (request.Canceled) return;
                }
                await output.WriteAsync(body.AsMemory(split), context.CancellationToken).ConfigureAwait(false);
            }));
            server.WithStaticFolder("/file", root, true, module =>
            {
                module.Cache = cache;
                module.ContentCaching = (trace.Seed & 1) == 0;
            });
            var running = server.RunAsync(stop.Token);
            Exception? failure = null;
            try
            {
                // Every trace, including a shrunk empty one, covers real overlapping actors
                // and learns opaque validators for each independently decoded representation.
                for (var actor = 0; actor < 3; actor++) await StartAsync(actor, actor, actor).ConfigureAwait(false);
                var ports = pending.Select(request => request?.RemotePort ?? 0).ToArray();
                Require(ports.All(port => port != 0) && (protocol == 3 || ports.Distinct().Count() == 3),
                    "response.physical-connections", "Actors did not establish independent connections.");
                trace.Cover("response.mandatory-three-actor-overlap");
                await FinishAllAsync().ConfigureAwait(false);
                foreach (var step in trace.Steps)
                {
                    token.ThrowIfCancellationRequested();
                    if (step.Command != 11)
                    {
                        await FinishAsync(step.Actor, false).ConfigureAwait(false);
                        await StartAsync(step.Actor, step.Command, step.Value).ConfigureAwait(false);
                    }
                    else
                    {
                        switch (step.Value % 4)
                        {
                            case 0: await FinishAsync(step.Actor, true).ConfigureAwait(false); break;
                            case 1: await FinishAsync(step.Actor, false).ConfigureAwait(false); break;
                            case 2: await FinishAllAsync().ConfigureAwait(false); break;
                            default:
                                await FinishAsync(step.Actor, false).ConfigureAwait(false);
                                clients[step.Actor].Dispose();
                                clients[step.Actor] = Client(protocol, certificate);
                                trace.Cover("response.replace-client");
                                break;
                        }
                    }
                    CheckFaults();
                }
                await FinishAllAsync().ConfigureAwait(false);
                // A final reused-connection response exposes leaked bytes/state from earlier
                // HEAD, 304, range, compression, cancellation or trailer completions.
                for (var actor = 0; actor < 3; actor++) await StartAsync(actor, 0, actor).ConfigureAwait(false);
                await FinishAllAsync().ConfigureAwait(false);
                CheckFaults();
                Require(server.State == WebServerState.Listening, "response.listener-survival", "The server stopped.");
                trace.Cover("response.healthy-followup");
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                var cleanupErrors = new List<Exception>();
                foreach (var request in all)
                {
                    request.Canceled = true;
                    request.Cancellation.Cancel();
                    request.Release.TrySetResult();
                    request.BodyRelease.TrySetResult();
                }
                stop.Cancel();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                foreach (var request in all)
                {
                    try
                    {
                        if (request.Response != null)
                        {
                            try { using var response = await request.Response.WaitAsync(cleanup.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!cleanup.IsCancellationRequested && request.Cancellation.IsCancellationRequested) { }
                        }
                        if (request.Entered.Task.IsCompletedSuccessfully)
                        {
                            await request.GateExited.Task.WaitAsync(cleanup.Token).ConfigureAwait(false);
                            await request.Closed.Task.WaitAsync(cleanup.Token).ConfigureAwait(false);
                            Require(request.CloseCount == 1, "response.close-once", request.Id);
                        }
                    }
                    catch (Exception error) when (error is StateMachineFailure or IOException or InvalidOperationException or OperationCanceledException or HttpRequestException or AggregateException)
                    { cleanupErrors.Add(error); }
                    finally { request.Dispose(); }
                }
                try { await running.WaitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cleanup.IsCancellationRequested && stop.IsCancellationRequested) { }
                catch (Exception error) when (error is StateMachineFailure or IOException or InvalidOperationException or OperationCanceledException or HttpRequestException or AggregateException)
                { cleanupErrors.Add(error); }
                foreach (var client in clients) client.Dispose();
                while (faults.TryDequeue(out var callbackFault)) cleanupErrors.Add(callbackFault);
                if (cleanupErrors.Count != 0)
                {
                    if (failure != null) cleanupErrors.Insert(0, failure);
                    throw new AggregateException("Response campaign cleanup failed.", cleanupErrors);
                }
            }

            async Task StartAsync(int actor, int mode, int value)
            {
                var id = actor + "-" + (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var spec = Spec.Create(mode, value, data, tags);
                var request = new Request(id, actor, value, spec, token);
                all.Add(request);
                requests.TryAdd(id, request);
                pending[actor] = request;
                request.Message.RequestUri = new Uri(url + (spec.Trailers ? "trailers" : "file/item.txt"));
                request.Message.Version = Version(protocol);
                request.Response = clients[actor].SendAsync(request.Message, HttpCompletionOption.ResponseHeadersRead, request.Cancellation.Token);
                await request.Entered.Task.WaitAsync(token).ConfigureAwait(false);
                trace.Cover("response.start." + spec.Name);
            }

            async Task FinishAsync(int actor, bool cancel)
            {
                var request = pending[actor];
                if (request == null) { trace.Cover("response.skip-empty"); return; }
                pending[actor] = null;
                if (cancel && request.Spec.Trailers && request.Value >= 32)
                {
                    // This is a real mid-response cancellation: the writer flushes a prefix
                    // and waits until the client has read a byte before it is released.
                    request.Release.TrySetResult();
                    await request.PartialWritten.Task.WaitAsync(token).ConfigureAwait(false);
                    using var response = await (request.Response ?? throw new InvalidOperationException("Missing response.")).ConfigureAwait(false);
                    Require(response.StatusCode == HttpStatusCode.OK && response.Version == Version(protocol), "response.partial-status", request.Id);
                    using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    Require(await input.ReadAsync(new byte[1], token).ConfigureAwait(false) == 1, "response.partial-prefix", request.Id);
                    request.Canceled = true;
                    request.Cancellation.Cancel();
                    request.BodyRelease.TrySetResult();
                    trace.Cover("response.cancel-flushed-prefix");
                }
                else
                {
                    request.Canceled = cancel;
                    if (cancel) request.Cancellation.Cancel();
                    request.BodyRelease.TrySetResult();
                    request.Release.TrySetResult();
                    try
                    {
                        using var response = await (request.Response ?? throw new InvalidOperationException("Missing response.")).ConfigureAwait(false);
                        Require(!cancel, "response.cancel-contract", request.Id);
                        await VerifyAsync(request, response).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancel && request.Cancellation.IsCancellationRequested) { }
                }
                await request.GateExited.Task.WaitAsync(token).ConfigureAwait(false);
                await request.Closed.Task.WaitAsync(token).ConfigureAwait(false);
                Require(request.CloseCount == 1, "response.close-once", request.Id);
                trace.Cover(cancel ? "response.cancel-entered" : "response.complete." + request.Spec.Name);
            }

            Task FinishAllAsync() => Task.WhenAll(Enumerable.Range(0, 3).Select(actor => FinishAsync(actor, false)));

            async Task VerifyAsync(Request request, HttpResponseMessage response)
            {
                var spec = request.Spec;
                var invariant = "response." + spec.Name;
                Require(response.Version == Version(protocol) && (int)response.StatusCode == spec.Status, invariant + ".status", request.Id + ": " + response.StatusCode);
                var declaredLength = response.Content.Headers.ContentLength;
                Require(!(response.Headers.TransferEncodingChunked == true && declaredLength.HasValue),
                    invariant + ".exclusive-framing", request.Id);
                if (protocol != 1) Require(!response.Headers.TransferEncoding.Any(), invariant + ".protocol-framing", request.Id);
                var wire = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), 131072, token).ConfigureAwait(false);
                if (spec.Head || spec.Status == 304)
                {
                    Require(wire.Length == 0, invariant + ".bodyless", request.Id);
                    if (spec.Head && spec.Coding == "identity")
                        Require(declaredLength == data.Length, invariant + ".head-length", request.Id);
                    else if (spec.Head && (trace.Seed & 1) == 0)
                        Require(declaredLength == representationLengths[spec.Coding], invariant + ".head-cached-length", request.Id);
                }
                else
                {
                    if (declaredLength is long length) Require(length == wire.Length, invariant + ".length", request.Id);
                    if (spec.Status != 416)
                    {
                        var encodings = response.Content.Headers.ContentEncoding.ToArray();
                        Require(encodings.Length <= 1, invariant + ".coding-count", request.Id);
                        var encoding = encodings.Length == 0 ? "identity" : encodings[0];
                        Require(encoding == spec.Coding, invariant + ".coding", request.Id);
                        var decoded = wire;
                        if (encoding != "identity")
                        {
                            using var input = new MemoryStream(wire);
                            using Stream decoder = encoding == "gzip" ? new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                            decoded = await ReadBoundedAsync(decoder, request.Body.Length, token).ConfigureAwait(false);
                        }
                        if (!decoded.AsSpan().SequenceEqual(request.Body))
                            throw new StateMachineFailure(invariant + ".body", request.Id + ": expected " + request.Body.Length + " bytes, received " + decoded.Length);
                    }
                }
                if (spec.Status == 206 || spec.Status == 416)
                    Require(response.Content.Headers.ContentRange?.ToString() == spec.ContentRange, invariant + ".range", request.Id);
                else Require(response.Content.Headers.ContentRange == null, invariant + ".no-range", request.Id);
                if (!spec.Trailers && spec.Status != 416)
                {
                    var tag = response.Headers.ETag?.ToString() ?? throw new StateMachineFailure(invariant + ".etag", request.Id);
                    if (tags.TryGetValue(spec.Coding, out var expected)) Require(tag == expected, invariant + ".validator-isolation", request.Id);
                    else Require(tags.TryAdd(spec.Coding, tag) || tags[spec.Coding] == tag, invariant + ".validator-publication", request.Id);
                    Require(response.Content.Headers.LastModified.HasValue, invariant + ".last-modified", request.Id);
                    if (!spec.Head && spec.Status == 200) representationLengths.TryAdd(spec.Coding, wire.Length);
                }
                var cookies = response.Headers.TryGetValues("Set-Cookie", out var cookieFields) ? cookieFields.ToArray() : Array.Empty<string>();
                Require(cookies.Length == 2 && cookies.Contains("raw=" + request.Id + "; Path=/")
                    && cookies.Contains("actor=" + request.Actor.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; Path=/"),
                    invariant + ".cookie-isolation", request.Id);
                if (spec.Trailers)
                {
                    Require(response.TrailingHeaders.TryGetValues("x-fuzz-end", out var trailers) && trailers.SequenceEqual(new[] { request.Id }), invariant + ".trailer-snapshot", request.Id);
                    Require(!response.Headers.Contains("x-fuzz-end"), invariant + ".trailer-separation", request.Id);
                    if ((request.Value & 1) != 0) Require(response.Headers.TryGetValues("X-Large", out var large) && large.SequenceEqual(new[] { new string('x', 4096) }), invariant + ".large-header", request.Id);
                }
                else Require(!response.TrailingHeaders.Any(), invariant + ".no-trailer-leak", request.Id);
            }

            void CheckFaults()
            {
                if (faults.TryDequeue(out var error)) ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--response-pipeline-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --response-pipeline-selftest");
        foreach (var protocol in new[] { 1, 2 })
        {
            foreach (var fault in new[] { "body", "cookie", "trailer" })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var trace = new StateMachineTrace("response-http" + protocol, 1731,
                    fault == "trailer" ? new[] { new StateMachineStep(0, 9, 1) } : Array.Empty<StateMachineStep>());
                var expected = fault == "trailer"
                    ? new[] { "response.trailers.trailer-snapshot" }
                    : new[] { "identity", "gzip", "deflate" }.Select(coding => "response.full-" + coding + (fault == "body" ? ".body" : ".cookie-isolation")).ToArray();
                string? rejected = null;
                try { await RunAsync(trace, protocol, deadline.Token, fault).ConfigureAwait(false); }
                catch (StateMachineFailure error) when (expected.Contains(error.Invariant)) { rejected = error.Invariant; }
                Require(rejected != null, "response.selftest-oracle", "Injected " + fault + " was not rejected by its specific invariant.");
                Console.WriteLine("Rejected injected " + fault + " on HTTP/" + protocol + " with " + rejected);
            }
        }
        return true;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(4096, limit + 1)];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), token).ConfigureAwait(false)) != 0)
        {
            Require(output.Length + read <= limit, "response.body-bound", "Response exceeded the independently expected bound.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static Version Version(int protocol) => protocol == 3 ? HttpVersion.Version30 : protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11;

    private static HttpClient Client(int protocol, X509Certificate2? certificate)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 1 };
        if (certificate != null) handler.SslOptions = new SslClientAuthenticationOptions
        { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() };
        return new HttpClient(handler) { DefaultRequestVersion = Version(protocol), DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact, Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void Require(bool condition, string invariant, string detail)
    {
        if (!condition) throw new StateMachineFailure(invariant, detail);
    }

    private sealed class Gate(Func<IHttpContext, Task> callback) : WebModuleBase("/")
    {
        public override bool IsFinalHandler => false;
        protected override Task OnRequestAsync(IHttpContext context) => callback(context);
    }

    private sealed record Spec(string Name, string Coding, int Status, byte[] Body, bool Head = false, bool Trailers = false,
        string? Range = null, string? ContentRange = null, string? IfNoneMatch = null, string? IfModifiedSince = null, string? IfRange = null)
    {
        internal static Spec Create(int mode, int value, byte[] data, IReadOnlyDictionary<string, string> tags)
        {
            var coding = mode is 1 or 2 ? (mode == 1 ? "gzip" : "deflate") : mode is 3 or 6 or 7 or 9 ? new[] { "identity", "gzip", "deflate" }[value % 3] : "identity";
            var start = data.Length == 0 ? 0 : value % data.Length;
            var end = Math.Max(0, Math.Min(data.Length - 1, start + 1 + value * 3));
            var range = "bytes=" + start + "-" + end;
            var contentRange = "bytes " + start + "-" + end + "/" + data.Length;
            return mode switch
            {
                3 => new("head", coding, 200, Array.Empty<byte>(), Head: true),
                4 when data.Length == 0 => new("range-empty", coding, 416, Array.Empty<byte>(), Range: range, ContentRange: "bytes */0"),
                4 when value % 3 == 1 => Suffix(value, data),
                4 when value % 3 == 2 => new("range-open", coding, 206, data[start..], Range: "bytes=" + start + "-", ContentRange: "bytes " + start + "-" + (data.Length - 1) + "/" + data.Length),
                4 => new("range", coding, 206, data[start..(end + 1)], Range: range, ContentRange: contentRange),
                5 => new("unsatisfiable", coding, 416, Array.Empty<byte>(), Range: "bytes=" + (data.Length + value) + "-", ContentRange: "bytes */" + data.Length),
                6 when value >= 48 => new("modified-since-stale", coding, 200, data, IfModifiedSince: "Thu, 03 Jan 2019 00:00:00 GMT"),
                6 when value >= 32 => new("modified-since", coding, 304, Array.Empty<byte>(), IfModifiedSince: "Sun, 03 Jan 2021 00:00:00 GMT"),
                6 when value >= 16 => new("conditional-range", "identity", 304, Array.Empty<byte>(), Range: range, IfNoneMatch: tags["identity"]),
                6 => new("not-modified", coding, 304, Array.Empty<byte>(), IfNoneMatch: (value & 1) == 0 ? tags[coding] : "*", IfModifiedSince: "Thu, 03 Jan 2019 00:00:00 GMT"),
                7 => new("validator-precedence", coding, 200, data, IfNoneMatch: "\"mismatch\"", IfModifiedSince: "Sun, 03 Jan 2021 00:00:00 GMT"),
                8 when (value & 1) == 0 && data.Length == 0 => new("if-range-empty", coding, 416, Array.Empty<byte>(), Range: range, ContentRange: "bytes */0", IfRange: tags[coding]),
                8 when value % 4 == 2 => new("if-range-date-match", coding, 206, data[start..(end + 1)], Range: range, ContentRange: contentRange, IfRange: "Thu, 02 Jan 2020 03:04:05 GMT"),
                8 when (value & 1) == 0 => new("if-range-match", coding, 206, data[start..(end + 1)], Range: range, ContentRange: contentRange, IfRange: tags[coding]),
                8 => new("if-range-miss", coding, 200, data, Range: range, IfRange: value % 4 == 3 ? "Thu, 03 Jan 2019 00:00:00 GMT" : "\"mismatch\""),
                9 => new("trailers", coding, 200, data, Trailers: true),
                10 when (value & 1) == 0 => new("forced-compression-range", "gzip", 200, data, Range: range),
                10 => new("unsupported-range-unit", coding, 200, data, Range: "items=0-1"),
                _ => new("full-" + coding, coding, 200, data),
            };
        }
    }

    private static Spec Suffix(int value, byte[] data)
    {
        var count = Math.Min(data.Length, 1 + value);
        var start = data.Length - count;
        return new Spec("range-suffix", "identity", 206, data[start..], Range: "bytes=-" + (1 + value),
            ContentRange: "bytes " + start + "-" + (data.Length - 1) + "/" + data.Length);
    }

    private sealed class Request : IDisposable
    {
        internal Request(string id, int actor, int value, Spec spec, CancellationToken token)
        {
            Id = id; Actor = actor; Value = value; Spec = spec;
            Body = spec.Trailers ? Encoding.ASCII.GetBytes(id + "|").Concat(spec.Body).ToArray() : spec.Body;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Message = new HttpRequestMessage(spec.Head ? HttpMethod.Head : HttpMethod.Get, (Uri?)null) { VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            Message.Headers.Add("X-Fuzz-Request", id);
            Message.Headers.Add("Cookie", "client=" + actor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Message.Headers.Add("Accept-Encoding", spec.Name == "forced-compression-range" ? "gzip, identity;q=0" : spec.Coding);
            if (spec.Range != null) Message.Headers.Add("Range", spec.Range);
            if (spec.IfNoneMatch != null) Message.Headers.Add("If-None-Match", spec.IfNoneMatch);
            if (spec.IfModifiedSince != null) Message.Headers.Add("If-Modified-Since", spec.IfModifiedSince);
            if (spec.IfRange != null) Message.Headers.Add("If-Range", spec.IfRange);
        }
        internal string Id { get; }
        internal int Actor { get; }
        internal int Value { get; }
        internal Spec Spec { get; }
        internal byte[] Body { get; }
        internal HttpRequestMessage Message { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource GateExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PartialWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BodyRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<HttpResponseMessage>? Response { get; set; }
        internal int RemotePort;
        internal volatile bool Canceled;
        internal int CloseCount;
        public void Dispose() { Message.Dispose(); Cancellation.Dispose(); }
    }
}
