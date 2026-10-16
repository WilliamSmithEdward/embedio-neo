using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Files;

// Mutations occur between completed cohorts: deterministic cache invalidation,
// not an assertion of atomic snapshots during arbitrary concurrent filesystem writes.
internal static class FileMutationFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        var root = Directory.CreateTempSubdirectory("embedio-file-mutation-fuzz-").FullName;
        var file = Path.Combine(root, "item.txt");
        var bytes = Array.Empty<byte>(); var exists = false; var version = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var cache = new FileCache();
        using var server = new WebServer(HttpListenerMode.EmbedIO, ConnectionIsolationFuzz.Prefix(false));
        var url = server.Options.UrlPrefixes.Single();
        var calls = new ConcurrentDictionary<string, Call>(StringComparer.Ordinal);
        var tasks = new List<Task>();
        var sequence = 0;
        server.PreferCompressionFor("text/plain");
        server.WithModule(new Gate(async context =>
        {
            var call = calls[context.Request.Headers["X-Mutation"] ?? throw new InvalidOperationException("Missing mutation id.")];
            context.OnClose(_ => { Interlocked.Increment(ref call.Closes); call.Closed.TrySetResult(); });
            call.Entered.TrySetResult();
            await call.Release.Task.WaitAsync(stop.Token).ConfigureAwait(false);
        }));
        server.WithStaticFolder("/", root, false, module => { module.Cache = cache; module.ContentCaching = true; });
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None })
        { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        var running = server.RunAsync(stop.Token);
        string? oldTag = null;
        var oldVersion = -1;
        try
        {
            // Same-size overwrites, delete/recreate and compressed cached variants are mandatory.
            Replace(17, 1); await Cohort(0).ConfigureAwait(false);
            Replace(17, 2); await Cohort(1).ConfigureAwait(false);
            File.Delete(file); exists = false; await Cohort(2).ConfigureAwait(false);
            Replace(0, 3); await Cohort(3).ConfigureAwait(false);
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                switch (step.Command % 4)
                {
                    case 0: Replace(new[] { 0, 1, 127, 4096, 65537 }[step.Value % 5], step.Value + step.Actor); break;
                    case 1: if (exists) { File.Delete(file); exists = false; } break;
                    case 2: Replace(bytes.Length, step.Value + 64); break;
                    default: break;
                }
                await Cohort(step.Value).ConfigureAwait(false);
                trace.Cover("files.mutation-" + step.Command % 4);
            }
        }
        finally
        {
            foreach (var call in calls.Values) call.Release.TrySetResult();
            stop.Cancel(); client.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                try { await Task.WhenAll(tasks).WaitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cleanup.IsCancellationRequested && stop.IsCancellationRequested) { }
            }
            finally
            {
                try { await running.WaitAsync(cleanup.Token).ConfigureAwait(false); }
                finally { server.Dispose(); cache.Dispose(); Directory.Delete(root, true); }
            }
        }

        void Replace(int size, int salt)
        {
            bytes = Enumerable.Range(0, size).Select(index => (byte)('a' + (index + salt) % 26)).ToArray();
            var actual = (byte[])bytes.Clone();
            if (injectFault && version == 0 && actual.Length > 0) actual[0] ^= 1;
            File.WriteAllBytes(file, actual);
            // No timestamp-granularity sleep: each generation gets a distinct exact timestamp.
            File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++version));
            exists = true; trace.Cover("files.replace-generation");
        }

        async Task Cohort(int value)
        {
            var prior = oldTag;
            var cohort = Enumerable.Range(0, 3).Select(_ => new Call()).ToArray();
            var pending = cohort.Select((call, actor) =>
            {
                var id = Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
                calls.TryAdd(id, call);
                var operation = Send(actor, value, prior, id); tasks.Add(operation); return operation;
            }).ToArray();
            try { await Task.WhenAll(cohort.Select(call => call.Entered.Task)).WaitAsync(token).ConfigureAwait(false); }
            finally { foreach (var call in cohort) call.Release.TrySetResult(); }
            trace.Cover("files.three-entered-requests");
            var results = await Task.WhenAll(pending).ConfigureAwait(false);
            await Task.WhenAll(cohort.Select(call => call.Closed.Task)).WaitAsync(token).ConfigureAwait(false);
            Require(cohort.All(call => call.Closes == 1), "files.context-close-once", value.ToString());
            if (exists) { oldTag = results[0]; oldVersion = version; }
            trace.Cover(exists ? "files.three-representations" : "files.deleted");
        }

        async Task<string?> Send(int actor, int value, string? prior, string id)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "item.txt");
            request.Headers.Add("X-Mutation", id);
            var coding = new[] { "identity", "gzip", "deflate" }[actor];
            request.Headers.Add("Accept-Encoding", coding);
            // An old tag may still legitimately match when no mutation occurred.
            if (actor == 0 && prior != null) request.Headers.Add("If-None-Match", prior);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
            if (!exists)
            {
                Require(response.StatusCode == HttpStatusCode.NotFound, "files.deleted-not-served", value.ToString());
                return null;
            }
            var tag = response.Headers.ETag?.ToString() ?? throw new StateMachineFailure("files.validator", "Missing ETag.");
            Require((response.StatusCode == HttpStatusCode.NotModified) == (actor == 0 && prior != null && oldVersion == version), "files.validator-freshness", tag);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                Require(actor == 0 && prior == tag && await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false) is { Length: 0 }, "files.conditional-bodyless", tag);
                return tag;
            }
            Require(response.StatusCode == HttpStatusCode.OK, "files.status", response.StatusCode.ToString());
            using var wire = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var encodedOutput = new MemoryStream();
            var wireBuffer = new byte[4096]; int wireCount;
            while ((wireCount = await wire.ReadAsync(wireBuffer, token).ConfigureAwait(false)) != 0)
            {
                Require(encodedOutput.Length + wireCount <= 131072, "files.encoded-bound", tag);
                encodedOutput.Write(wireBuffer, 0, wireCount);
            }
            var encoded = encodedOutput.ToArray();
            var actualCoding = response.Content.Headers.ContentEncoding.SingleOrDefault() ?? "identity";
            Require(actualCoding == coding, "files.coding", actualCoding);
            byte[] decoded;
            if (coding == "identity") decoded = encoded;
            else
            {
                using var input = new MemoryStream(encoded);
                using Stream decoder = coding == "gzip" ? new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[4096]; int count;
                while ((count = await decoder.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    Require(output.Length + count <= bytes.Length, "files.decoded-bound", tag);
                    output.Write(buffer, 0, count);
                }
                decoded = output.ToArray();
            }
            Require(decoded.AsSpan().SequenceEqual(bytes), "files.current-representation", tag);
            return tag;
        }
    }
    private sealed class Call
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Closes;
    }
    private sealed class Gate(Func<IHttpContext, Task> callback) : WebModuleBase("/")
    {
        public override bool IsFinalHandler => false;
        protected override Task OnRequestAsync(IHttpContext context) => callback(context);
    }
    private static void Require(bool condition, string invariant, string detail)
    { if (!condition) throw new StateMachineFailure(invariant, detail); }
}
