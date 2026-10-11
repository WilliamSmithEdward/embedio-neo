using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Sessions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #43 evaluates async server lifetime APIs. These cases pin the lifetime
    // contract of WebServer: how stop, cancellation, drain and disposal differ,
    // what repeat calls do, and which work the server cancels when it ends.
    public class Issue43_ServerLifetime
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [TestCase("cancel")]
        [TestCase("dispose")]
        [TestCase("drain")]
        public async Task StateChangesAreReportedOnceInOrder(string shutdown)
        {
            var transitions = new List<(WebServerState, WebServerState)>();
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding));
            server.StateChanged += (_, e) => { lock (transitions) transitions.Add((e.OldState, e.NewState)); };
            using var stop = new CancellationTokenSource();
            Assert.That(server.State, Is.EqualTo(WebServerState.Created));
            var running = server.RunAsync(stop.Token);
            Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            using (var client = new HttpClient { Timeout = Deadline })
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("ok"));
            switch (shutdown)
            {
                case "cancel": stop.Cancel(); break;
                case "dispose": server.Dispose(); break;
                default: await server.DrainAsync(Deadline); break;
            }
            await running.WaitAsync(Deadline);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
            Assert.That(transitions, Is.EqualTo(new[]
            {
                (WebServerState.Created, WebServerState.Loading),
                (WebServerState.Loading, WebServerState.Listening),
                (WebServerState.Listening, WebServerState.Stopped),
            }));
        }

        [Test]
        public async Task RunAsyncCannotBeRepeatedOrRestarted()
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            Assert.That(() => server.RunAsync(), Throws.InvalidOperationException.With.Message.Contains("already"));
            stop.Cancel();
            await running.WaitAsync(Deadline);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
            Assert.That(() => server.RunAsync(), Throws.InvalidOperationException.With.Message.Contains("restarted"),
                "A stopped server is final; the message says so instead of claiming it is still running.");
            // The port is free for a replacement instance.
            using var replacement = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("replacement", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stopReplacement = new CancellationTokenSource();
            var runningReplacement = replacement.RunAsync(stopReplacement.Token);
            try
            {
                using var client = new HttpClient { Timeout = Deadline };
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("replacement"));
            }
            finally
            {
                stopReplacement.Cancel();
                await runningReplacement.WaitAsync(Deadline);
            }
        }

        [Test]
        public async Task DisposeIsIdempotentAndSafeUnderConcurrentShutdown()
        {
            var url = Resources.GetServerAddress();
            var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            var drain = server.DrainAsync(Deadline);
            await Task.WhenAll(
                Task.Run(server.Dispose),
                Task.Run(server.Dispose),
                Task.Run(stop.Cancel),
                Task.Run(() => server.Listener.Stop())).WaitAsync(Deadline);
            await running.WaitAsync(Deadline);
            try { await drain.WaitAsync(Deadline); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
            Assert.That(server.Listener.IsListening, Is.False);
            Assert.That(server.Dispose, Throws.Nothing, "Disposal after completion is a no-op.");
        }

        [Test]
        public void DisposeBeforeRunAsyncFailsWithoutBindingThePort()
        {
            var url = Resources.GetServerAddress();
            var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            server.Dispose();
            Assert.That(async () => await server.RunAsync(), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(server.Listener.IsListening, Is.False);
            Assert.That(server.State, Is.Not.EqualTo(WebServerState.Listening));
        }

        [Test]
        public async Task PreCanceledTokenStopsWithoutBindingThePort()
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            await server.RunAsync(new CancellationToken(true)).WaitAsync(Deadline);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
            Assert.That(server.Listener.IsListening, Is.False);
        }

        [Test]
        public async Task DisposalCancelsTheTokenHandedToModulesAndSessions()
        {
            var module = new TokenModule();
            var sessions = new TokenSessionManager();
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)).WithModule(module);
            server.SessionManager = sessions;
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            Assert.That(module.Token.CanBeCanceled, Is.True);
            Assert.That(sessions.Token.CanBeCanceled, Is.True);
            Assert.That(module.Token.IsCancellationRequested, Is.False);
            server.Dispose();
            await running.WaitAsync(Deadline);
            Assert.That(module.Token.IsCancellationRequested, Is.True, "Module background work ends with the server.");
            Assert.That(sessions.Token.IsCancellationRequested, Is.True, "Session purging ends with the server.");
            Assert.That(stop.IsCancellationRequested, Is.False, "The caller's token is never canceled by the server.");
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task DisposalCancelsInFlightHandlers(int protocol)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, context.CancellationToken); exited.TrySetResult(false); }
                    catch (OperationCanceledException) { exited.TrySetResult(true); }
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = CreateClient(protocol);
            var pending = client.GetAsync(url);
            try
            {
                await entered.Task.WaitAsync(Deadline);
                server.Dispose();
                await running.WaitAsync(Deadline);
                Assert.That(await exited.Task.WaitAsync(Deadline), Is.True, "The handler observes disposal through its context token.");
                Assert.That(async () => await pending, Throws.InstanceOf<HttpRequestException>().Or.InstanceOf<OperationCanceledException>());
            }
            finally
            {
                stop.Cancel();
                try { await pending; } catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
        }

        [Test]
        public async Task CancellationCancelsInFlightHandlersBeforeRunAsyncCompletes()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, context.CancellationToken); exited.TrySetResult(false); }
                    catch (OperationCanceledException) { exited.TrySetResult(true); }
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = CreateClient(1);
            var pending = client.GetAsync(url);
            try
            {
                await entered.Task.WaitAsync(Deadline);
                stop.Cancel();
                Assert.That(await exited.Task.WaitAsync(Deadline), Is.True);
                await running.WaitAsync(Deadline);
            }
            finally
            {
                try { await pending; } catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
        }

        [Test]
        public async Task ListenerStopEndsRunAsyncAndCancelsRemainingHandlers()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, context.CancellationToken); exited.TrySetResult(false); }
                    catch (OperationCanceledException) { exited.TrySetResult(true); }
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = CreateClient(1);
            var pending = client.GetAsync(url);
            try
            {
                await entered.Task.WaitAsync(Deadline);
                server.Listener.Stop();
                await running.WaitAsync(Deadline);
                Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
                Assert.That(await exited.Task.WaitAsync(Deadline), Is.True, "Work the server left behind is canceled once RunAsync completes.");
            }
            finally
            {
                stop.Cancel();
                try { await pending; } catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
        }

        [Test]
        public async Task DrainCompletesInFlightRequestsAndRefusesNewConnections()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync("drained", "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = CreateClient(1);
            var pending = client.GetStringAsync(url);
            try
            {
                await entered.Task.WaitAsync(Deadline);
                var drain = server.DrainAsync(Deadline);
                Assert.That(drain.IsCompleted, Is.False, "Drain waits for the in-flight request.");
                var address = new Uri(url);
                using (var late = new TcpClient())
                {
                    var refused = false;
                    try
                    {
                        await late.ConnectAsync(address.Host, address.Port).WaitAsync(Deadline);
                        await late.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {address.Authority}\r\n\r\n"));
                        var buffer = new byte[1];
                        using var timeout = new CancellationTokenSource(Deadline);
                        refused = await late.GetStream().ReadAsync(buffer, 0, 1, timeout.Token) == 0;
                    }
                    catch (Exception error) when (error is SocketException or System.IO.IOException) { refused = true; }
                    Assert.That(refused, Is.True, "A draining server does not serve new connections.");
                }
                release.TrySetResult();
                Assert.That(await pending.WaitAsync(Deadline), Is.EqualTo("drained"));
                await drain.WaitAsync(Deadline);
                await running.WaitAsync(Deadline);
                Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
                Assert.That(stop.IsCancellationRequested, Is.False);
            }
            finally
            {
                release.TrySetResult();
                stop.Cancel();
                try { await pending; } catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
        }

        [Test]
        public void DisposalRacingRunCompletionNeverThrows()
        {
            // TestWebServer blocks its run on a wait handle that its own disposal
            // releases, so disposal and run completion end the lifetime concurrently.
            // Neither side may dispose the lifetime source while the other cancels it.
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var server = new EmbedIO.Testing.TestWebServer();
                server.Start();
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening), "iteration " + iteration);
                Assert.That(server.Dispose, Throws.Nothing, "iteration " + iteration);
            }
        }

        private static HttpClient CreateClient(int protocol) => new(new SocketsHttpHandler { UseProxy = false })
        {
            DefaultRequestVersion = protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = Deadline,
        };

        private sealed class TokenModule : WebModuleBase
        {
            public TokenModule() : base("/") { }
            public CancellationToken Token { get; private set; }
            public override bool IsFinalHandler => false;
            protected override void OnStart(CancellationToken cancellationToken) => Token = cancellationToken;
            protected override Task OnRequestAsync(IHttpContext context) => Task.CompletedTask;
        }

        private sealed class TokenSessionManager : ISessionManager
        {
            public CancellationToken Token { get; private set; }
            public void Start(CancellationToken cancellationToken) => Token = cancellationToken;
            public ISession Create(IHttpContext context) => throw new NotSupportedException();
            public void Delete(IHttpContext context, string id) { }
            public void OnContextClose(IHttpContext context) { }
        }
    }
}
