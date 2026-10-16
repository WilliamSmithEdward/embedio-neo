using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Testing;

internal static class RequestCodingFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        var url = ConnectionIsolationFuzz.Prefix(false);
        using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)
            .WithSupportCompressedRequests(true).WithMaximumRequestBodyBytes(8192).WithMaximumDecompressedRequestBodyBytes(1024));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var calls = new ConcurrentDictionary<string, Call>(StringComparer.Ordinal);
        var pending = new ConcurrentBag<Task>();
        server.WithModule(new ActionModule("/", HttpVerbs.Post, async context =>
        {
            var call = calls[context.Request.Headers["X-Body"] ?? throw new InvalidOperationException("Missing body id.")];
            context.OnClose(_ => { Interlocked.Increment(ref call.Closes); call.Closed.TrySetResult(); });
            call.Entered.TrySetResult();
            await call.Release.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            using var input = context.OpenRequestStream();
            using var decoded = new MemoryStream();
            var buffer = new byte[call.Chunk]; int count;
            while ((count = await input.ReadAsync(buffer, context.CancellationToken).ConfigureAwait(false)) != 0)
            {
                Require(decoded.Length + count <= 1024, "coding.decoded-bound", call.Id);
                decoded.Write(buffer, 0, count);
            }
            Interlocked.Increment(ref call.Accepted);
            var hash = Convert.ToHexString(SHA256.HashData(decoded.ToArray()));
            await context.SendStringAsync(injectFault ? "corrupted" : hash, "text/plain", WebServer.Utf8NoBomEncoding).ConfigureAwait(false);
        }));
        var running = server.RunAsync(stop.Token);
        try
        {
            for (var mode = 0; mode < 12; mode++) await Batch(mode, mode).ConfigureAwait(false);
            foreach (var step in trace.Steps) await Batch(step.Command, step.Value + step.Actor).ConfigureAwait(false);
            await Batch(0, 63).ConfigureAwait(false);
            trace.Cover("coding.healthy-followup");
        }
        finally
        {
            foreach (var call in calls.Values) call.Release.TrySetResult();
            stop.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await Task.WhenAll(pending).WaitAsync(cleanup.Token).ConfigureAwait(false); }
            finally { await running.WaitAsync(cleanup.Token).ConfigureAwait(false); }
        }
        async Task Batch(int mode, int value)
        {
            var group = Enumerable.Range(0, 3).Select(actor => new Call(calls.Count + "-" + actor, 1 + (value + actor) % 129)).ToArray();
            var tasks = group.Select((call, actor) =>
            {
                calls.TryAdd(call.Id, call); var task = Send(call, mode, value + actor); pending.Add(task); return task;
            }).ToArray();
            try { await Task.WhenAll(group.Select(call => call.Entered.Task)).WaitAsync(token).ConfigureAwait(false); }
            finally { foreach (var call in group.Reverse()) call.Release.TrySetResult(); }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            trace.Cover("coding.three-overlapping-requests"); trace.Cover("coding.mode-" + mode);
        }
        async Task Send(Call call, int mode, int value)
        {
            var size = mode == 7 ? 1025 : mode == 8 ? 1024 : mode == 9 ? 0 : new[] { 0, 1, 127, 128, 1023 }[value % 5];
            var body = Enumerable.Range(0, size).Select(index => (byte)((index * 31 + value) & 255)).ToArray();
            var chain = mode switch
            {
                0 => Array.Empty<string>(),
                1 or 7 or 8 or 9 => new[] { "gzip" },
                2 => new[] { "deflate" },
                3 or 11 => new[] { "br" },
                4 => new[] { "gzip", "br" },
                5 => new[] { "deflate", "gzip" },
                6 => new[] { "unknown" },
                _ => new[] { "br", "deflate" }
            };
            var wire = body;
            foreach (var coding in chain.Where(coding => coding != "unknown"))
            {
                using var output = new MemoryStream();
                using (Stream encoder = coding == "gzip" ? new GZipStream(output, CompressionMode.Compress, true)
                    : coding == "deflate" ? new DeflateStream(output, CompressionMode.Compress, true) : new BrotliStream(output, CompressionMode.Compress, true))
                    encoder.Write(wire);
                wire = output.ToArray();
                // BCL gzip/deflate writers are lazy: writing zero bytes can emit no envelope.
                // Use complete canonical empty members rather than send a malformed body.
                if (wire.Length == 0) wire = coding == "gzip"
                    ? Convert.FromHexString("1F8B08000000000000FF03000000000000000000") : new byte[] { 3, 0 };
            }
            if (mode == 11) wire = wire[..^1];
            using var request = new HttpRequestMessage(HttpMethod.Post, "") { Content = new ByteArrayContent(wire) };
            request.Headers.Add("X-Body", call.Id);
            foreach (var coding in chain) request.Content.Headers.ContentEncoding.Add(coding);
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            var status = mode == 7 ? 413 : mode is 6 or 11 ? 400 : 200;
            Require((int)response.StatusCode == status, "coding.expected-status", mode + ": " + response.StatusCode);
            if (status == 200)
                Require(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false) == Convert.ToHexString(SHA256.HashData(body)), "coding.body-hash", call.Id);
            await call.Closed.Task.WaitAsync(token).ConfigureAwait(false);
            Require(call.Closes == 1 && call.Accepted == (status == 200 ? 1 : 0), "coding.acceptance-lifetime", call.Id);
        }
    }
    private sealed class Call(string id, int chunk)
    {
        internal string Id { get; } = id;
        internal int Chunk { get; } = chunk;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Closes, Accepted;
    }
    private static void Require(bool condition, string invariant, string detail)
    { if (!condition) throw new StateMachineFailure(invariant, detail); }
}
