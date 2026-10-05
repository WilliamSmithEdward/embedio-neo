using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue599_MultipleStaticFolders
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task OneRootMountAlreadyServesNestedAnalyticsFolder(bool cache)
        {
            using var folders = new Folders();
            var nested = Path.Combine(folders.Root, "analytics");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "index.html"), "nested analytics");
            using var server = new TestWebServer();
            server.WithStaticFolder("/", folders.Root, true, m => m.WithContentCaching(cache)).Start();

            Assert.That(await server.Client.GetStringAsync("/analytics/"), Is.EqualTo("nested analytics"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DefaultRootMountStopsLaterFolderAndFallback(bool cache)
        {
            using var folders = new Folders();
            using var server = new TestWebServer();
            var fallbackCalled = false;
            server.WithStaticFolder("/", folders.Root, true, m => m.WithContentCaching(cache))
                .WithStaticFolder("/", folders.Analytics, true, m => m.WithContentCaching(cache))
                .OnAny(context =>
                {
                    fallbackCalled = true;
                    return context.SendDataAsync(new { Message = "fallback" });
                }).Start();

            Assert.That(await server.Client.GetStringAsync("/shared.txt"), Is.EqualTo("root"));
            using var response = await server.Client.GetAsync("/analytics-only.txt");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(fallbackCalled, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SpecificMountBeforeRootPreservesApiAndFolderIsolation(bool cache)
        {
            using var folders = new Folders();
            using var server = new TestWebServer();
            server.WithWebApi("/api", m => m.WithController<PingController>())
                .WithStaticFolder("/analytics", folders.Analytics, true, m => m.WithContentCaching(cache))
                .WithStaticFolder("/", folders.Root, true, m => m.WithContentCaching(cache))
                .Start();

            Assert.That(await server.Client.GetStringAsync("/"), Is.EqualTo("root index"));
            Assert.That(await server.Client.GetStringAsync("/analytics/"), Is.EqualTo("analytics index"));
            Assert.That(await server.Client.GetStringAsync("/shared.txt"), Is.EqualTo("root"));
            Assert.That(await server.Client.GetStringAsync("/analytics/shared.txt"), Is.EqualTo("analytics"));
            using var api = JsonDocument.Parse(await server.Client.GetStringAsync("/api/ping"));
            Assert.That(api.RootElement.GetString(), Is.EqualTo("api"));
            using var missing = await server.Client.GetAsync("/analytics/root-only.txt");
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExplicitPassThroughEnablesOrderedOverlayAndFinalFallback(bool cache)
        {
            using var folders = new Folders();
            using var server = new TestWebServer();
            var fallbackCalls = 0;
            server.WithWebApi("/api", m => m.WithController<PingController>())
                .WithStaticFolder("/", folders.Root, true, m => m
                    .WithContentCaching(cache).HandleMappingFailed(FileRequestHandler.PassThrough))
                .WithStaticFolder("/", folders.Analytics, true, m => m
                    .WithContentCaching(cache).HandleMappingFailed(FileRequestHandler.PassThrough))
                .OnAny(context =>
                {
                    fallbackCalls++;
                    context.Response.StatusCode = 404;
                    return context.SendDataAsync(new { Message = "fallback" });
                }).Start();

            // Repeated requests also exercise each module's separate content cache.
            for (var request = 0; request < 2; request++)
            {
                Assert.That(await server.Client.GetStringAsync("/shared.txt"), Is.EqualTo("root"));
                Assert.That(await server.Client.GetStringAsync("/analytics-only.txt"), Is.EqualTo("analytics only"));
            }

            using var api = JsonDocument.Parse(await server.Client.GetStringAsync("/api/ping"));
            Assert.That(api.RootElement.GetString(), Is.EqualTo("api"));
            Assert.That(fallbackCalls, Is.Zero);
            using var missing = await server.Client.GetAsync("/missing.txt");
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            using var error = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.That(error.RootElement.GetProperty("Message").GetString(), Is.EqualTo("fallback"));
            Assert.That(fallbackCalls, Is.EqualTo(1));
        }

        public class PingController : WebApiController
        {
            [Route(HttpVerbs.Get, "/ping")]
            public string Ping() => "api";
        }

        private sealed class Folders : IDisposable
        {
            private readonly string _parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            public Folders()
            {
                Root = Path.Combine(_parent, "root");
                Analytics = Path.Combine(_parent, "analytics");
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(Analytics);
                File.WriteAllText(Path.Combine(Root, "index.html"), "root index");
                File.WriteAllText(Path.Combine(Analytics, "index.html"), "analytics index");
                File.WriteAllText(Path.Combine(Root, "shared.txt"), "root");
                File.WriteAllText(Path.Combine(Analytics, "shared.txt"), "analytics");
                File.WriteAllText(Path.Combine(Root, "root-only.txt"), "root only");
                File.WriteAllText(Path.Combine(Analytics, "analytics-only.txt"), "analytics only");
            }

            public string Root { get; }

            public string Analytics { get; }

            public void Dispose() => Directory.Delete(_parent, true);
        }
    }
}
