using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue588_StreamingClose
    {
        [Test]
        public async Task FlushFailureStillRunsAsyncCompletionAndCloseCallbacksInOrder()
        {
            var order = new System.Collections.Generic.List<int>();
            await TestWebServer.UseAsync(server => server.WithAction("/", HttpVerbs.Get, context =>
            {
                context.OnRequestCompleted(() => { order.Add(1); return Task.CompletedTask; });
                context.OnClose(_ => order.Add(2));
                context.OnClose(_ => throw new InvalidOperationException("deliberate callback failure"));
                context.Response.OutputStream.Dispose();
                return Task.CompletedTask;
            }), async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That(order, Is.EqualTo(new[] { 1, 2 }));
            });
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task CloseCallbackFollowsStreamingHandlerCompletion(HttpListenerMode mode, bool cancelServer)
        {
            var scenario = new Scenario();
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/", module => module.WithController(() => new StreamingController(scenario)));
            using var stop = new CancellationTokenSource();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var running = server.RunAsync(stop.Token);
            try
            {
                using var response = await client.GetAsync(url + "events", HttpCompletionOption.ResponseHeadersRead);
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                Assert.That(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("data: ready"));
                await scenario.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(scenario.Closed.Task.IsCompleted, Is.False, "OnClose is not a signal to terminate an active handler.");
                Assert.That(scenario.Token, Is.EqualTo(stop.Token), "The context token belongs to RunAsync.");
                if (cancelServer) stop.Cancel();
                else scenario.Release.TrySetResult();
                await scenario.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await scenario.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(scenario.CloseCount, Is.EqualTo(1));
                if (!cancelServer)
                    Assert.That(await client.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
            }
            finally
            {
                scenario.Release.TrySetResult();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task ClientResetDoesNotCancelServerTokenAndWriteErrorsRespectConfiguration(HttpListenerMode mode, bool ignoreWrites)
        {
            var scenario = new Scenario { WriteAfterRelease = true };
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/", module => module.WithController(() => new StreamingController(scenario)));
            server.Listener.IgnoreWriteExceptions = ignoreWrites;
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var socket = new TcpClient();
            try
            {
                var address = new Uri(url);
                await socket.ConnectAsync(address.Host, address.Port);
                await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /events HTTP/1.1\r\nHost: {address.Authority}\r\n\r\n"));
                using var reader = new StreamReader(socket.GetStream(), leaveOpen: true);
                string? line;
                do
                {
                    line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(line, Is.Not.Null, "The server must emit the initial event before the reset.");
                }
                while (line != "data: ready");
                await scenario.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                socket.Client.LingerState = new LingerOption(true, 0);
                socket.Close();
                scenario.Release.TrySetResult();
                await scenario.WritesFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(scenario.Token.IsCancellationRequested, Is.False);
                if (!ignoreWrites)
                    Assert.That(scenario.WriteFailure, Is.Not.Null, "Strict writes must surface the reset.");
                else if (mode == HttpListenerMode.EmbedIO)
                    Assert.That(scenario.WriteFailure, Is.Null, "The managed listener suppresses transport write failures.");
                // The native listener may dispose its stream despite IgnoreWriteExceptions.
                Assert.That(scenario.Closed.Task.IsCompleted, Is.False, "Transport failures do not complete a still-running handler.");
                scenario.Finish.TrySetResult();
                await scenario.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(scenario.CloseCount, Is.EqualTo(1));
                using var healthy = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                healthy.DefaultRequestHeaders.ConnectionClose = true;
                for (var request = 0; request < 3; request++)
                    Assert.That(await healthy.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                scenario.Release.TrySetResult();
                scenario.Finish.TrySetResult();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public sealed class Scenario
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource WritesFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken Token { get; set; }
            public bool WriteAfterRelease { get; set; }
            public Exception? WriteFailure { get; set; }
            public int CloseCount;
        }

        public sealed class StreamingController : WebApiController
        {
            private readonly Scenario _scenario;

            public StreamingController(Scenario scenario) => _scenario = scenario;

            [Route(HttpVerbs.Get, "/health")]
            public Task Health() => HttpContext.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);

            [Route(HttpVerbs.Get, "/events")]
            public async Task Events()
            {
                Response.ContentType = "text/event-stream";
                Response.SendChunked = true;
                _scenario.Token = CancellationToken;
                HttpContext.OnClose(_ =>
                {
                    Interlocked.Increment(ref _scenario.CloseCount);
                    _scenario.Closed.TrySetResult();
                });
                try
                {
                    var frame = Encoding.UTF8.GetBytes("data: ready\n\n");
                    await Response.OutputStream.WriteAsync(frame, 0, frame.Length, CancellationToken);
                    await Response.OutputStream.FlushAsync(CancellationToken);
                    _scenario.Started.TrySetResult();
                    await _scenario.Release.Task.WaitAsync(CancellationToken);
                    if (_scenario.WriteAfterRelease)
                    {
                        try
                        {
                            var heartbeat = Encoding.UTF8.GetBytes(":" + new string(' ', 65536) + "\n\n");
                            for (var write = 0; write < 100; write++)
                            {
                                await Response.OutputStream.WriteAsync(heartbeat, 0, heartbeat.Length, CancellationToken);
                                await Response.OutputStream.FlushAsync(CancellationToken);
                                await Task.Delay(10, CancellationToken);
                            }
                        }
                        catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException)
                        {
                            _scenario.WriteFailure = error;
                        }
                        finally { _scenario.WritesFinished.TrySetResult(); }
                        await _scenario.Finish.Task.WaitAsync(CancellationToken);
                    }
                }
                finally { _scenario.Exited.TrySetResult(); }
            }
        }
    }
}
