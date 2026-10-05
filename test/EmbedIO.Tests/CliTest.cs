using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Cli;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed class CliTest
    {
        private string _root = null!;

        [SetUp]
        public void SetUp() => _root = Directory.CreateTempSubdirectory("embedio-cli-test-").FullName;

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        [Test]
        public void DefaultOptionsAndWwwrootSelectionMatchArchivedCli()
        {
            var options = Options.Parse(Array.Empty<string>());
            Assert.That(options.Port, Is.EqualTo(9696));
            Assert.That(options.NoWatch, Is.False);
            Assert.That(options.ResolveRoot(_root), Is.EqualTo(_root));
            var wwwroot = Directory.CreateDirectory(Path.Combine(_root, "wwwroot")).FullName;
            Assert.That(options.ResolveRoot(_root), Is.EqualTo(wwwroot));
            Assert.That(Options.Parse(new[] { "-p", "." }).ResolveRoot(_root), Is.EqualTo(_root));
        }

        [TestCase("-p", "-o", "-a")]
        [TestCase("--path", "--port", "--api")]
        public void OriginalOptionAliasesAreSupported(string path, string port, string api)
        {
            var options = Options.Parse(new[] { path, "folder with spaces", port, "12345", api, "plugin.dll", "--no-watch" });
            Assert.That(options.RootPath, Is.EqualTo("folder with spaces"));
            Assert.That(options.Port, Is.EqualTo(12345));
            Assert.That(options.ApiPath, Is.EqualTo("plugin.dll"));
            Assert.That(options.NoWatch, Is.True);
        }

        [TestCase("--unknown")]
        [TestCase("--path")]
        [TestCase("--port=0")]
        [TestCase("--port=65536")]
        [TestCase("--port=65535")]
        [TestCase("--port=oops")]
        [TestCase("--no-watch=false")]
        public void InvalidArgumentsAreRejected(string argument)
            => Assert.Throws<ArgumentException>(() => Options.Parse(new[] { argument }));

        [Test]
        public void LastPortIsAllowedWithoutWatcher()
            => Assert.That(Options.Parse(new[] { "--port=65535", "--no-watch" }).Port, Is.EqualTo(65535));

        [Test]
        public async Task StaticFilesHeadListingsAndMissingFilesWorkWithoutWatch()
        {
            const string html = "<html><body>héllo</body></html>";
            File.WriteAllText(Path.Combine(_root, "index.html"), html);
            Directory.CreateDirectory(Path.Combine(_root, "files"));
            File.WriteAllText(Path.Combine(_root, "files", "a.txt"), "content");
            await WithHost(new Options(NoWatch: true), async (host, client) =>
            {
                Assert.That(await client.GetStringAsync("/"), Is.EqualTo(html));
                Assert.That(await client.GetStringAsync("/files/a.txt"), Is.EqualTo("content"));
                Assert.That(await client.GetStringAsync("/files/"), Does.Contain("a.txt"));
                using var missing = await client.GetAsync("/missing");
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/index.html"));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(head.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
                File.WriteAllText(Path.Combine(_root, "index.html"), "changed length");
                Assert.That(await client.GetStringAsync("/"), Is.EqualTo("changed length"));
            });
        }

        [Test]
        public async Task WatchModeInjectsHtmlAndNotifiesForNestedFileChanges()
        {
            File.WriteAllText(Path.Combine(_root, "index.html"), "<HTML><BODY>live</BODY></HTML>");
            var nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
            await WithHost(new Options(), async (host, client) =>
            {
                var html = await client.GetStringAsync("/");
                Assert.That(html, Does.Contain("/watcher").And.Contain("location.reload()"));
                using var socket = new ClientWebSocket();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(new Uri($"ws://localhost:{new Uri(host.Url).Port + 1}/watcher"), timeout.Token);
                File.WriteAllText(Path.Combine(nested, "changed.txt"), "reload");
                var buffer = new byte[128];
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                Assert.That(Encoding.UTF8.GetString(buffer, 0, received.Count), Is.EqualTo("{\"Update\":true}"));
                socket.Abort();
            });
        }

        [Test]
        public async Task PluginsAndStaticFilesShareCoreAndRoutes()
        {
            File.WriteAllText(Path.Combine(_root, "index.html"), "static fallback");
            var plugin = typeof(EmbedIO.Cli.TestPlugin.PingController).Assembly.Location;
            await WithHost(new Options(RootPath: _root, ApiPath: plugin, NoWatch: true), async (host, client) =>
            {
                Assert.That(await client.GetStringAsync("/cli-ping"), Does.Contain("pong"));
                Assert.That(await client.GetStringAsync("/"), Is.EqualTo("static fallback"));
                using var socket = new ClientWebSocket();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(new Uri(host.Url.Replace("http:", "ws:") + "cli-echo"), timeout.Token);
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("echo")), WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[32];
                var response = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                Assert.That(Encoding.UTF8.GetString(buffer, 0, response.Count), Is.EqualTo("echo"));
                socket.Abort();
            });
        }

        [Test]
        public async Task ApiOnlyDoesNotExposeWorkingDirectory()
        {
            File.WriteAllText(Path.Combine(_root, "secret.txt"), "private");
            await WithHost(new Options(ApiPath: typeof(EmbedIO.Cli.TestPlugin.PingController).Assembly.Location, NoWatch: true), async (_, client) =>
            {
                Assert.That(await client.GetStringAsync("/cli-ping"), Does.Contain("pong"));
                using var response = await client.GetAsync("/secret.txt");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            });
        }

        [Test]
        public void MissingPluginPathFailsClearly()
            => Assert.Throws<FileNotFoundException>(() => CliHost.Create(new Options(ApiPath: "missing.dll", NoWatch: true), _root));

        [Test]
        public async Task PluginDirectoryLoadsAlongsideDependencies()
        {
            // Keep loaded DLL fixtures in build output: collectible contexts may
            // retain their file handles until the runtime releases compiled delegates.
            var directory = Path.Combine(AppContext.BaseDirectory, "CliPlugins");
            await WithHost(new Options(ApiPath: directory, NoWatch: true), async (_, client) =>
                Assert.That(await client.GetStringAsync("/cli-ping"), Does.Contain("pong")));
        }

        [Test]
        public void EmptyPluginDirectoryFailsRatherThanPretendingToLoad()
            => Assert.Throws<InvalidOperationException>(() => CliHost.Create(new Options(ApiPath: _root, NoWatch: true), _root));

        [Test]
        public async Task WatcherPortConflictStopsMainServerAndReportsFailure()
        {
            var port = FreePortPair();
            using var occupied = new TcpListener(Dns.GetHostAddresses("localhost")[0], port + 1);
            occupied.ExclusiveAddressUse = true;
            occupied.Start();
            await using var host = CliHost.Create(new Options(Port: port, NoBrowser: true), _root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThatAsync(async () => await host.RunAsync(timeout.Token), Throws.Exception);
            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Stopped));
        }

        private async Task WithHost(Options options, Func<CliHost, HttpClient, Task> action)
        {
            var port = FreePortPair();
            await using var host = CliHost.Create(options with { Port = port, NoBrowser = true }, _root);
            using var stop = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = host.RunAsync(stop.Token, () => ready.TrySetResult());
            try
            {
                var first = await Task.WhenAny(ready.Task, running).WaitAsync(TimeSpan.FromSeconds(10));
                await first;
                Assert.That(ready.Task.IsCompletedSuccessfully, Is.True, "Server stopped before listening");
                using var client = new HttpClient { BaseAddress = new Uri(host.Url), Timeout = TimeSpan.FromSeconds(10) };
                await action(host, client);
            }
            finally
            {
                await stop.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private static int FreePortPair()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var first = new TcpListener(IPAddress.Loopback, 0);
                first.Start();
                var port = ((IPEndPoint)first.LocalEndpoint).Port;
                if (port == 65535) continue;
                using var second = new TcpListener(IPAddress.Loopback, port + 1);
                try { second.Start(); return port; }
                catch (SocketException) { }
            }
            throw new InvalidOperationException("Cannot allocate adjacent test ports.");
        }
    }
}
