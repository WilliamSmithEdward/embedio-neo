using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Files;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue490_SpaRoutes
    {
        [TestCase(HttpListenerMode.EmbedIO, false, "/")]
        [TestCase(HttpListenerMode.Microsoft, false, "/")]
        [TestCase(HttpListenerMode.EmbedIO, true, "/dashboard?tab=recent")]
        [TestCase(HttpListenerMode.Microsoft, true, "/dashboard?tab=recent")]
        [TestCase(HttpListenerMode.EmbedIO, false, "/dashboard/reports")]
        [TestCase(HttpListenerMode.Microsoft, false, "/dashboard/reports")]
        [TestCase(HttpListenerMode.EmbedIO, true, "/release/v1.0")]
        [TestCase(HttpListenerMode.Microsoft, true, "/release/v1.0")]
        public async Task DirectNavigationAndRefreshPreserveUrlAndFileHeaders(HttpListenerMode mode, bool cache, string path)
            => await WithServer(mode, cache, async (client, root) =>
            {
                for (var refresh = 0; refresh < 2; refresh++)
                {
                    using var response = await client.GetAsync(path);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("<h1>SPA</h1>"));
                    Assert.That(response.Headers.Location, Is.Null);
                    Assert.That(response.RequestMessage!.RequestUri!.PathAndQuery, Is.EqualTo(path));
                    Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/html"));
                    Assert.That(response.Headers.ETag, Is.Not.Null);
                    Assert.That(response.Content.Headers.LastModified, Is.Not.Null);
                    Assert.That(response.Headers.CacheControl!.MustRevalidate, Is.True);
                }
            });

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task AssetsApiAndMissingResourcesDoNotBecomeHtml(HttpListenerMode mode, bool cache)
            => await WithServer(mode, cache, async (client, root) =>
            {
                Assert.That(await client.GetStringAsync("/api/ping"), Is.EqualTo("api"));
                Assert.That(await client.GetStringAsync("/app.js"), Is.EqualTo("console.log('asset');"));
                Assert.That(await client.GetStringAsync("/login"), Is.EqualTo("real file wins"));
                foreach (var path in new[] { "/unknown", "/missing.js", "/missing.png", "/api/missing", "/Dashboard", "/dashboard/index.html" })
                {
                    using var response = await client.GetAsync(path);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), path);
                    Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("<h1>SPA</h1>"), path);
                }
            });

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task HeadConditionalAndRangeUseTheEntryFile(HttpListenerMode mode, bool cache)
            => await WithServer(mode, cache, async (client, root) =>
            {
                using var get = await client.GetAsync("/dashboard");
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/dashboard"));
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(get.Content.Headers.ContentLength));
                Assert.That(head.Headers.ETag, Is.EqualTo(get.Headers.ETag));
                using var conditional = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
                conditional.Headers.IfNoneMatch.Add(get.Headers.ETag!);
                using var unchanged = await client.SendAsync(conditional);
                Assert.That(unchanged.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                Assert.That(await unchanged.Content.ReadAsByteArrayAsync(), Is.Empty);
                using var range = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
                range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
                using var partial = await client.SendAsync(range);
                Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                Assert.That(await partial.Content.ReadAsStringAsync(), Is.EqualTo("<h1>"));
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task InvalidMethodsMissingEntryAndDefaultProviderRemainUnchanged(HttpListenerMode mode)
            => await WithServer(mode, false, async (client, root) =>
            {
                foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
                {
                    using var response = await client.SendAsync(new HttpRequestMessage(method, "/dashboard"));
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
                }
                using var ordinary = new FileSystemProvider(root, true);
                Assert.That(ordinary.MapUrlPath("/dashboard", new MockMimeTypeProvider()), Is.Null);
                using var missing = new SpaFiles(Path.Combine(root, "empty"), "/dashboard");
                Assert.That(missing.MapUrlPath("/dashboard", new MockMimeTypeProvider()), Is.Null);
                using var provider = new SpaFiles(root, "/dashboard");
                foreach (var path in new[] { "/../secret.txt", "/%2e%2e/secret.txt", "/dashboard/../../secret.txt", "/%5c%5csecret.txt" })
                    Assert.That(provider.MapUrlPath(path, new MockMimeTypeProvider()), Is.Null, path);
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task MountedSpaUsesModuleRelativeRoutes(HttpListenerMode mode)
            => await WithServer(mode, true, async (client, root) =>
            {
                using var response = await client.GetAsync("/app/dashboard?tab=recent");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("<h1>SPA</h1>"));
                Assert.That(response.Headers.Location, Is.Null);
                using var missing = await client.GetAsync("/dashboard");
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }, "/app");

        private static async Task WithServer(HttpListenerMode mode, bool cache, Func<HttpClient, string, Task> check, string mount = "/")
        {
            var parent = Path.Combine(Path.GetTempPath(), "embedio-spa-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(parent, "wwwroot");
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            File.WriteAllText(Path.Combine(parent, "secret.txt"), "outside root");
            File.WriteAllText(Path.Combine(root, "index.html"), "<h1>SPA</h1>", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "app.js"), "console.log('asset');", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "login"), "real file wins", new UTF8Encoding(false));
            var url = TestObjects.Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            try
            {
                using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                    .WithModule(new ActionModule("/api", HttpVerbs.Any, ctx =>
                    {
                        if (ctx.RequestedPath != "/ping") throw HttpException.NotFound();
                        return ctx.SendStringAsync("api", "text/plain", Encoding.UTF8);
                    }))
                    .WithModule(new FileModule(mount, new SpaFiles(root, "/dashboard", "/dashboard/reports", "/release/v1.0", "/login"))
                    { Cache = new FileCache { MaxFileSizeKb = cache ? 200 : 0 } });
                var running = server.RunAsync(stop.Token);
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(15) };
                try { await check(client, root); }
                finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
            finally { Directory.Delete(parent, true); }
        }
    }
}

namespace EmbedIO.Tests.Issues
{
    // Application-owned policy for a fixed, immutable deployment.
    internal sealed class SpaFiles : IFileProvider, IDisposable
    {
        private readonly FileSystemProvider _files;
        private readonly HashSet<string> _routes;

        public SpaFiles(string root, params string[] routes)
        {
            _files = new FileSystemProvider(root, isImmutable: true);
            _routes = new HashSet<string>(routes, StringComparer.Ordinal);
        }

        public bool IsImmutable => _files.IsImmutable;
        public event Action<string>? ResourceChanged
        {
            add => _files.ResourceChanged += value;
            remove => _files.ResourceChanged -= value;
        }

        public void Start(CancellationToken cancellationToken) => _files.Start(cancellationToken);
        public Stream OpenFile(string path) => _files.OpenFile(path);
        public IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mimeTypes)
            => _files.GetDirectoryEntries(path, mimeTypes);
        public void Dispose() => _files.Dispose();

        public MappedResourceInfo? MapUrlPath(string path, IMimeTypeProvider mimeTypes)
        {
            var actual = _files.MapUrlPath(path, mimeTypes);
            if (actual != null || !_routes.Contains(path))
                return actual;
            return _files.MapUrlPath("/index.html", mimeTypes);
        }
    }
}
