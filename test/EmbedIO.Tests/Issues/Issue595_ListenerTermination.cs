using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue595_ListenerTermination
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ClosingIdleListenerCompletesServerRunAndReportsStopped(bool dispose)
        {
            using var stopping = new CancellationTokenSource();
            using var server = new WebServer(options => options
                .WithUrlPrefix(Resources.GetServerAddress()).WithMode(HttpListenerMode.EmbedIO));
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, e) =>
            {
                if (e.NewState == WebServerState.Stopped) stopped.TrySetResult();
            };
            var running = server.RunAsync(stopping.Token);
            try
            {
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
                if (dispose) server.Dispose();
                else server.Listener.Stop();
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Observe(running).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(running.IsCompleted, Is.True);
                Assert.That(server.Listener.IsListening, Is.False);
                Assert.That(stopping.IsCancellationRequested, Is.False);
            }
            finally
            {
                stopping.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) when (running.IsFaulted) { /* Termination can fault the accept task. */ }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClosingListenerWakesEveryPendingAccept(bool dispose)
        {
            using var listener = new Net.HttpListener();
            listener.AddPrefix(Resources.GetServerAddress());
            listener.Start();
            using var cleanup = new CancellationTokenSource();
            var accepts = Enumerable.Range(0, 8).Select(_ => listener.GetContextAsync(cleanup.Token)).ToArray();
            try
            {
                if (dispose) listener.Dispose();
                else listener.Stop();
                var errors = await Task.WhenAll(accepts.Select(Observe)).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(errors.All(error => error is HttpListenerException { ErrorCode: 995 }), Is.True);
                Assert.That(listener.IsListening, Is.False);
            }
            finally { cleanup.Cancel(); }
        }

        [Test]
        public async Task CancelingOneAcceptKeepsListenerUsableAndPreservesCallerToken()
        {
            using var listener = new Net.HttpListener();
            var url = Resources.GetServerAddress();
            listener.AddPrefix(url);
            listener.Start();
            using var canceled = new CancellationTokenSource();
            var accept = listener.GetContextAsync(canceled.Token);
            canceled.Cancel();
            var error = await Observe(accept).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(error, Is.InstanceOf<OperationCanceledException>());
            Assert.That(((OperationCanceledException)error!).CancellationToken, Is.EqualTo(canceled.Token));
            Assert.That(listener.IsListening, Is.True);
            await ServeOne(listener, url);
        }

        [Test]
        public async Task ListenerCanRestartAfterStopWithoutRevivingOldAccepts()
        {
            using var listener = new Net.HttpListener();
            var url = Resources.GetServerAddress();
            listener.AddPrefix(url);
            listener.Start();
            var oldAccept = listener.GetContextAsync(CancellationToken.None);
            listener.Stop();
            listener.Start();
            Assert.That(await Observe(oldAccept).WaitAsync(TimeSpan.FromSeconds(2)), Is.InstanceOf<HttpListenerException>());
            await ServeOne(listener, url);
        }

        private static async Task ServeOne(Net.HttpListener listener, string url)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var accepted = listener.GetContextAsync(timeout.Token);
            var response = client.GetAsync(url);
            var context = await accepted;
            context.Response.StatusCode = 204;
            context.Response.ContentLength64 = 0;
            context.Response.OutputStream.Flush();
            context.Close();
            using var result = await response;
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        [Test]
        public async Task ConcurrentAcceptsDrainEveryRequestWithoutLosingQueueSignals()
        {
            using var listener = new Net.HttpListener();
            var url = Resources.GetServerAddress();
            listener.AddPrefix(url);
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            for (var batch = 0; batch < 20; batch++)
            {
                var consumers = Enumerable.Range(0, 16).Select(async _ =>
                {
                    var context = await listener.GetContextAsync(timeout.Token).ConfigureAwait(false);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(context.Request.Url.AbsolutePath);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    context.Close();
                }).ToArray();
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, 16).Select(async item =>
                {
                    var path = $"batch{batch}/item{item}";
                    Assert.That(await client.GetStringAsync(url + path), Is.EqualTo("/" + path));
                }));
                }
                catch { TestContext.WriteLine($"batch={batch}, consumers=" + string.Join(";", consumers.Select(task => task.Status + ":" + task.Exception))); throw; }
                await Task.WhenAll(consumers).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Test]
        public async Task StopDisposeAndCallerCancellationRaceAlwaysCompleteAccept()
        {
            for (var iteration = 0; iteration < 40; iteration++)
            {
                using var listener = new Net.HttpListener();
                listener.AddPrefix(Resources.GetServerAddress());
                listener.Start();
                using var canceled = new CancellationTokenSource();
                var accepted = listener.GetContextAsync(canceled.Token);
                await Task.WhenAll(Task.Run(listener.Stop), Task.Run(listener.Dispose), Task.Run(canceled.Cancel))
                    .WaitAsync(TimeSpan.FromSeconds(5));
                var error = await Observe(accepted).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(error is HttpListenerException or ObjectDisposedException or OperationCanceledException, Is.True);
                Assert.That(listener.IsListening, Is.False);
            }
        }

        private static async Task<Exception?> Observe(Task task)
        {
            try { await task; return null; }
            catch (Exception error) { return error; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ShutdownDrainsAContextWhoseConnectionClosedBeforeRegistration(bool dispose)
        {
            using var listener = new Net.HttpListener();
            var address = new Uri(Resources.GetServerAddress());
            listener.AddPrefix(address.ToString());
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = new TcpClient();
            var accept = listener.GetContextAsync(timeout.Token);
            await socket.ConnectAsync(address.Host, address.Port);
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {address.Authority}\r\n\r\n"));
            var context = await accept;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var connection = context.GetType().GetProperty("Connection", flags)!.GetValue(context)!;
            // Model timeout/disconnection between unbinding and late queue registration.
            connection.GetType().GetMethod("CloseSocket", flags)!.Invoke(connection, null);
            connection.GetType().GetMethod("Unbind", flags)!.Invoke(connection, null);
            typeof(Net.HttpListener).GetMethod("RegisterContext", flags)!.Invoke(listener, new object[] { context });
            var queue = (System.Collections.IDictionary)typeof(Net.HttpListener).GetField("_ctxQueue", flags)!.GetValue(listener)!;
            var disposing = Task.Run(() =>
            {
                if (dispose) listener.Dispose();
                else listener.Stop();
            });
            try
            {
                await disposing.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(queue.Count, Is.Zero);
            }
            finally
            {
                // Ensure a failing old implementation cannot spin forever in the test process.
                queue.Clear();
                await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.That(listener.IsListening, Is.False);
        }
    }
}
