using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Files;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue129_Customization
    {
        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task MimeProviderKeepsLocalServerBuiltinAndUnknownFallbacks(HttpListenerMode mode)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            foreach (var name in new[] { "value.custom", "local.html", "server.server", "builtin.txt", "unknown.zzzzneo" })
                File.WriteAllText(Path.Combine(directory, name), "content");
            var provider = new MimeProvider("application/x-provider");
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.WithCustomMimeType(".server", "application/x-server");
            var files = new FileModule("/files", new FileSystemProvider(directory, true)) { MimeTypeProvider = provider };
            files.WithCustomMimeType(".html", "text/x-local");
            server.WithModule(files);
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                foreach (var row in new[] { ("value.custom", "application/x-provider"), ("local.html", "text/x-local"), ("server.server", "application/x-server"), ("builtin.txt", "text/plain"), ("unknown.zzzzneo", "application/octet-stream") })
                {
                    using var response = await client.GetAsync(url + "files/" + row.Item1);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo(row.Item2));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("content"));
                }
                Assert.Throws<InvalidOperationException>(() => files.MimeTypeProvider = null);
                Assert.That(provider.Disposals, Is.Zero);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
            Assert.That(provider.Disposals, Is.Zero);
        }

        [TestCase(HttpListenerMode.EmbedIO, true, false)]
        [TestCase(HttpListenerMode.Microsoft, true, false)]
        [TestCase(HttpListenerMode.EmbedIO, false, false)]
        [TestCase(HttpListenerMode.Microsoft, false, false)]
        [TestCase(HttpListenerMode.EmbedIO, true, true)]
        [TestCase(HttpListenerMode.Microsoft, true, true)]
        public async Task ProviderCompressionAndExplicitOverridesAreNegotiated(HttpListenerMode mode, bool preferred, bool localOverride)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var payload = new string('x', 8192);
            File.WriteAllText(Path.Combine(directory, "value.custom"), payload);
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.PreferCompression("*/*", true);
            var files = new FileModule("/", new FileSystemProvider(directory, true)) { MimeTypeProvider = new MimeProvider("text/x-provider", preferred) };
            if (localOverride) files.PreferCompression("text/*", false);
            server.WithModule(files);
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });
                using var request = new HttpRequestMessage(HttpMethod.Get, url + "value.custom");
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentEncoding.Contains("gzip"), Is.EqualTo(preferred && !localOverride));
                using var body = await response.Content.ReadAsStreamAsync();
                using var decoded = preferred && !localOverride ? new System.IO.Compression.GZipStream(body, System.IO.Compression.CompressionMode.Decompress) : body;
                using var reader = new StreamReader(decoded);
                Assert.That(await reader.ReadToEndAsync(), Is.EqualTo(payload));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task WarmMappingAndContentCachesRemainModuleLocal(HttpListenerMode mode)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "value.custom"), "shared file");
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            foreach (var row in new[] { ("/one", "application/x-one"), ("/two", "application/x-two") })
                server.WithModule(new FileModule(row.Item1, new FileSystemProvider(directory, true)) { MimeTypeProvider = new MimeProvider(row.Item2) });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                for (var i = 0; i < 4; i++)
                {
                    foreach (var row in new[] { ("one", "application/x-one"), ("two", "application/x-two") })
                    {
                        using var response = await client.GetAsync(url + row.Item1 + "/value.custom");
                        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo(row.Item2));
                        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("shared file"));
                    }
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
        }

        [Test]
        public void ProviderCyclesAndNullCallbacksAreRejected()
        {
            using var first = new FileModule("/one", new FileSystemProvider(Path.GetTempPath(), true));
            using var second = new FileModule("/two", new FileSystemProvider(Path.GetTempPath(), true));
            Assert.That(first.MimeTypeProvider, Is.Null);
            Assert.Throws<ArgumentException>(() => first.MimeTypeProvider = first);
            first.MimeTypeProvider = second;
            Assert.Throws<ArgumentException>(() => second.MimeTypeProvider = first);
            first.MimeTypeProvider = null;
            Assert.Throws<ArgumentNullException>(() => new PreRequestModule(null!));
            Assert.Throws<ArgumentNullException>(() => new PreRequestModule("/", null!));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task UnknownExtensionDefaultsAreApplicationPolicyAndDoNotChangeOtherModules(HttpListenerMode mode)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "value.zzzzneo"), "unknown");
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.WithModule(new FileModule("/custom", new FileSystemProvider(directory, true)) { MimeTypeProvider = new DefaultProvider() });
            server.WithModule(new FileModule("/original", new FileSystemProvider(directory, true)));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                using var custom = await client.GetAsync(url + "custom/value.zzzzneo");
                using var original = await client.GetAsync(url + "original/value.zzzzneo");
                Assert.That(custom.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/x-app-default"));
                Assert.That(original.Content.Headers.ContentType!.MediaType, Is.EqualTo(MimeType.Default));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task CallbackCancellationIsObservedWithoutDispatchingDownstream(HttpListenerMode mode)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.WithModule(new PreRequestModule(async context =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                finally { exited.TrySetResult(); }
            }));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }));
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var request = client.GetAsync(url);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                stop.Cancel();
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                try { using var response = await request; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                Assert.That(calls, Is.Zero);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task PreRequestCallbacksAreAwaitedInOrderAndPreserveRequestData(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.WithModule(new PreRequestModule(async context =>
            {
                await Task.Delay(10, context.CancellationToken);
                context.Items["trace"] = "first";
            }));
            server.WithModule(new PreRequestModule("/api", context =>
            {
                context.Items["trace"] += "|second";
                return Task.CompletedTask;
            }));
            server.WithModule(new ActionModule("/api", HttpVerbs.Any, context => context.SendStringAsync(
                context.Items["trace"] + "|" + context.Request.RawUrl + "|" + context.Request.QueryString["tag"], "text/plain", Encoding.UTF8)));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                Assert.That(await client.GetStringAsync(url + "api/MiXeD?tag=XyZ"), Is.EqualTo("first|second|/api/MiXeD?tag=XyZ|XyZ"));
                using var wrongMount = await client.GetAsync(url + "API/MiXeD?tag=XyZ");
                Assert.That(wrongMount.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, "handled", HttpStatusCode.Accepted)]
        [TestCase(HttpListenerMode.Microsoft, "handled", HttpStatusCode.Accepted)]
        [TestCase(HttpListenerMode.EmbedIO, "denied", HttpStatusCode.Forbidden)]
        [TestCase(HttpListenerMode.Microsoft, "denied", HttpStatusCode.Forbidden)]
        [TestCase(HttpListenerMode.EmbedIO, "error", HttpStatusCode.InternalServerError)]
        [TestCase(HttpListenerMode.Microsoft, "error", HttpStatusCode.InternalServerError)]
        public async Task HandledRejectedOrFaultedCallbacksStopDispatchAndNextRequestStaysHealthy(HttpListenerMode mode, string action, HttpStatusCode expected)
        {
            var url = Resources.GetServerAddress();
            var calls = 0;
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.WithModule(new PreRequestModule(async context =>
            {
                if (context.Request.QueryString["action"] == null) return;
                if (action == "denied") throw HttpException.Forbidden();
                if (action == "error") throw new InvalidOperationException("Expected callback failure.");
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                await context.SendStringAsync("early", "text/plain", Encoding.UTF8);
                context.SetHandled();
            }));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, context =>
            {
                Interlocked.Increment(ref calls);
                return context.SendStringAsync("healthy", "text/plain", Encoding.UTF8);
            }));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(url + "?action=" + action);
                Assert.That(response.StatusCode, Is.EqualTo(expected));
                Assert.That(calls, Is.Zero);
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("healthy"));
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task ConcurrentCallbacksPreserveBodiesAndRequestItemsAndFinalHandlersStopLaterCallbacks(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            var lateCalls = 0;
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.WithModule(new PreRequestModule(async context =>
            {
                context.Items["id"] = context.Request.QueryString["id"]!;
                await Task.Delay(10, context.CancellationToken);
            }));
            server.WithModule(new ActionModule("/", HttpVerbs.Post, async context =>
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                await context.SendStringAsync(context.Items["id"] + "|" + body, "text/plain", Encoding.UTF8);
            }));
            server.WithModule(new PreRequestModule(context =>
            {
                Interlocked.Increment(ref lateCalls);
                return Task.CompletedTask;
            }));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                await Task.WhenAll(Enumerable.Range(0, 12).Select(async id =>
                {
                    using var content = new StringContent("MiXeD-" + id, Encoding.UTF8, "text/plain");
                    using var response = await client.PostAsync(url + "?id=" + id, content);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(id + "|MiXeD-" + id));
                }));
                Assert.That(lateCalls, Is.Zero);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private sealed class MimeProvider : IMimeTypeProvider, IDisposable
        {
            private readonly string _type;
            private readonly bool? _compression;
            public int Disposals;
            public MimeProvider(string type, bool? compression = null) { _type = type; _compression = compression; }
            public string GetMimeType(string extension) => extension is ".custom" or ".html" ? _type : null!;
            public bool TryDetermineCompression(string mimeType, out bool preferred) { preferred = _compression ?? false; return _compression.HasValue; }
            public void Dispose() => Disposals++;
        }
        private sealed class DefaultProvider : IMimeTypeProvider
        {
            public string GetMimeType(string extension) => MimeType.Associations.ContainsKey(extension) ? null! : "application/x-app-default";
            public bool TryDetermineCompression(string mimeType, out bool preferred) { preferred = false; return false; }
        }
    }
}
