using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue595_TwoServers
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task StaticAndApiServersSurviveRepeatedConcurrentTrafficAndIndependentShutdown(bool sharedPort)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), "frontend");
            var frontUrl = Resources.GetServerAddress();
            var apiUrl = sharedPort ? frontUrl : Resources.GetServerAddress();
            using var front = new Host(new WebServer(options => options
                .WithUrlPrefix(frontUrl + "front/").WithMode(HttpListenerMode.EmbedIO))
                .WithStaticFolder("/front", directory, false));
            using var api = new Host(new WebServer(options => options
                .WithUrlPrefix(apiUrl + "api/").WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/api", HttpVerbs.Get, async context =>
                {
                    await Task.Yield();
                    await context.SendStringAsync("backend", "text/plain", WebServer.Utf8NoBomEncoding);
                })));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                async Task Pair()
                {
                    Assert.That(await client.GetStringAsync(frontUrl + "front/index.html"), Is.EqualTo("frontend"));
                    Assert.That(await client.GetStringAsync(apiUrl + "api/value"), Is.EqualTo("backend"));
                }

                for (var batch = 0; batch < 20; batch++)
                    await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Pair()));
                Assert.That(front.Server.State, Is.EqualTo(WebServerState.Listening));
                Assert.That(api.Server.State, Is.EqualTo(WebServerState.Listening));
                api.Server.Dispose();
                await api.ObserveCompletion();
                Assert.That(api.Server.State, Is.EqualTo(WebServerState.Stopped));
                Assert.That(await client.GetStringAsync(frontUrl + "front/index.html"), Is.EqualTo("frontend"));
                Assert.That(front.Server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public async Task IncompleteAndDisconnectedClientsDoNotStopHealthyRequests()
        {
            var url = Resources.GetServerAddress();
            using var host = new Host(new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, context =>
                    context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding))));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var address = new Uri(url);
            for (var batch = 0; batch < 20; batch++)
            {
                using var socket = new TcpClient();
                await socket.ConnectAsync(address.Host, address.Port);
                await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost:"));
                socket.Client.LingerState = new LingerOption(true, 0);
                socket.Close();
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("healthy"));
            }

            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
        }

        [Test]
        public async Task CloseCallbackErrorsAreIsolatedFromTheListener()
        {
            var url = Resources.GetServerAddress();
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var host = new Host(new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    context.OnClose(_ =>
                    {
                        observed.TrySetResult();
                        throw new InvalidOperationException("deliberate close callback failure");
                    });
                    return context.SendStringAsync("sent", "text/plain", WebServer.Utf8NoBomEncoding);
                })));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Assert.That(await client.GetStringAsync(url), Is.EqualTo("sent"));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await client.GetStringAsync(url), Is.EqualTo("sent"));
            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
        }

        [Test]
        public async Task FatalListenerCleanupCompletesAcceptLoopAndReportsStopped()
        {
            using var server = new FatalServer(Resources.GetServerAddress());
            using var host = new Host(server);
            server.TriggerFatalCleanup();
            await host.ObserveCompletion();
            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Stopped));
            Assert.That(host.Server.Listener.IsListening, Is.False);
        }

        private sealed class FatalServer : WebServer
        {
            public FatalServer(string url) : base(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)) { }
            public void TriggerFatalCleanup() => OnFatalException();
        }

        [Test]
        public async Task DisconnectAfterDispatchDoesNotStopOtherRequests()
        {
            var url = Resources.GetServerAddress();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var host = new Host(new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/abort")
                    {
                        entered.TrySetResult();
                        try
                        {
                            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            await context.SendStringAsync(new string('x', 65536), "text/plain", WebServer.Utf8NoBomEncoding);
                        }
                        finally { handled.TrySetResult(); }
                    }
                    else await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                })));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var socket = new TcpClient();
            var address = new Uri(url);
            try
            {
                await socket.ConnectAsync(address.Host, address.Port);
                await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /abort HTTP/1.1\r\nHost: {address.Authority}\r\n\r\n"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                socket.Client.LingerState = new LingerOption(true, 0);
                socket.Close();
                release.TrySetResult();
                await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (var request = 0; request < 10; request++)
                    Assert.That(await client.GetStringAsync(url), Is.EqualTo("healthy"));
                Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { release.TrySetResult(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClosingDuringInFlightRequestsCompletesAcceptLoop(bool dispose)
        {
            var url = Resources.GetServerAddress();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var host = new Host(new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync("done", "text/plain", WebServer.Utf8NoBomEncoding);
                })));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var requests = Enumerable.Range(0, 16).Select(async _ =>
            {
                try { using var response = await client.GetAsync(url); }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) { }
            }).ToArray();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(() =>
                {
                    if (dispose) host.Server.Dispose();
                    else host.Server.Listener.Stop();
                }).WaitAsync(TimeSpan.FromSeconds(5));
                await host.ObserveCompletion();
                Assert.That(host.Server.State, Is.EqualTo(WebServerState.Stopped));
                Assert.That(host.Server.Listener.IsListening, Is.False);
            }
            finally
            {
                release.TrySetResult();
                await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private sealed class Host : IDisposable
        {
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;
            public WebServer Server { get; }

            public Host(WebServer server)
            {
                Server = server;
                _running = server.RunAsync(_stop.Token);
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }

            public async Task ObserveCompletion()
            {
                try { await _running.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) when (_running.IsFaulted) { }
            }

            public void Dispose()
            {
                _stop.Cancel();
                try { ObserveCompletion().GetAwaiter().GetResult(); }
                finally { Server.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
