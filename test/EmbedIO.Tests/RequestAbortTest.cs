using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Health;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class RequestAbortTest
    {
        private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static async Task WithServer(Func<IHttpContext, Task> application, Func<int, CancellationTokenSource, Task> verify, TimeSpan? requestTimeout = null)
        {
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(options => options.WithUrlPrefix($"http://127.0.0.1:{port}/").WithRequestTimeout(requestTimeout)).WithModule(new ActionModule(context => application(context)));
            using var health = new HealthCheckService(server);
            var running = server.RunAsync(stop.Token);
            try
            {
                await health.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                await verify(port, stop);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        private static async Task<Socket> Request(int port, string path = "hold", string extra = "")
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                var request = Encoding.ASCII.GetBytes($"GET /{path} HTTP/1.1\r\nHost: 127.0.0.1\r\n{extra}\r\n");
                var sent = 0;
                while (sent != request.Length) sent += await socket.SendAsync(request.AsMemory(sent), SocketFlags.None);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }
        private static Task Canceled(CancellationToken token)
        {
            var signal = Signal<bool>();
            token.Register(() => signal.TrySetResult(true));
            return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ResetCancelsOnlyTheOptedInToken(bool inputEnd)
        {
            var entered = Signal<(CancellationToken Abort, CancellationToken Existing, IHttpContext Context)>();
            await WithServer(async context =>
            {
                var token = context.GetRequestAbortedToken(inputEnd);
                entered.TrySetResult((token, context.CancellationToken, context));
                await Task.Delay(Timeout.Infinite, token);
            }, async (port, _) =>
            {
                using var socket = await Request(port);
                var request = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                socket.LingerState = new LingerOption(true, 0);
                socket.Dispose();
                await Canceled(request.Abort);
                Assert.That(request.Existing.IsCancellationRequested, Is.False);
                Assert.That(request.Context.GetRequestAbortedToken(inputEnd), Is.EqualTo(request.Abort));

            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CanceledTokenRetainsItsSignaledWaitHandleAndLateCallbacks(bool inputEnd)
        {
            var entered = Signal<CancellationToken>();
            var closed = Signal<bool>();
            await WithServer(async context =>
            {
                var token = context.GetRequestAbortedToken(inputEnd);
                context.OnClose(_ => closed.TrySetResult(true));
                entered.TrySetResult(token);
                await Task.Delay(Timeout.Infinite, token);
            }, async (port, _) =>
            {
                using var socket = await Request(port);
                var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var waitHandle = token.WaitHandle;
                socket.LingerState = new LingerOption(true, 0);
                socket.Dispose();
                await Canceled(token);
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Completion can precede the cancellation worker leaving its last callback.
                // Keep using the retained token/handle across that asynchronous handoff.
                for (var iteration = 0; iteration < 20; iteration++)
                {
                    await Task.Delay(10);
                    Assert.That(waitHandle.WaitOne(0), Is.True);
                    Assert.That(token.WaitHandle, Is.SameAs(waitHandle));
                    var calls = 0;
                    using var registration = token.Register(() => Interlocked.Increment(ref calls));
                    Assert.That(calls, Is.EqualTo(1));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HalfClosePreservesDefaultTokenAndOnlyCancelsExplicitInputEndMode(bool acquireStrictFirst)
        {
            var entered = Signal<(CancellationToken Normal, CancellationToken Strict)>();
            var finish = Signal<bool>();
            await WithServer(async context =>
            {
                var first = context.GetRequestAbortedToken(acquireStrictFirst);
                var second = context.GetRequestAbortedToken(!acquireStrictFirst);
                entered.TrySetResult(acquireStrictFirst ? (second, first) : (first, second));
                await finish.Task;
                context.Response.KeepAlive = false;
                context.Response.ContentLength64 = 5;
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("hello"));
            }, async (port, _) =>
            {
                using var socket = await Request(port);
                var tokens = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    using var stream = new NetworkStream(socket, false);
                    socket.Shutdown(SocketShutdown.Send);
                    await Canceled(tokens.Strict);
                    Assert.That(tokens.Normal.IsCancellationRequested, Is.False);
                    finish.TrySetResult(true);
                    using var reader = new StreamReader(stream);
                    var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(response, Does.Contain("hello"));
                    Assert.That(tokens.Normal.IsCancellationRequested, Is.False);
                }
                finally { finish.TrySetResult(true); }
            });
        }

        [Test]
        public async Task DefaultHalfCloseReceivesACompleteResponseAndTokenStaysHealthyAfterStop()
        {
            var entered = Signal<CancellationToken>();
            var release = Signal<bool>();
            CancellationToken captured = default;
            await WithServer(async context =>
            {
                entered.TrySetResult(context.GetRequestAbortedToken());
                await release.Task;
                context.Response.KeepAlive = false;
                context.Response.ContentLength64 = 5;
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("hello"));
            }, async (port, _) =>
            {
                using var socket = await Request(port);
                captured = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    using var stream = new NetworkStream(socket, false);
                    socket.Shutdown(SocketShutdown.Send);
                    await Task.Delay(120);
                    Assert.That(captured.IsCancellationRequested, Is.False);
                    release.TrySetResult(true);
                    using var reader = new StreamReader(stream);
                    Assert.That(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)), Does.EndWith("hello"));
                }
                finally { release.TrySetResult(true); }
            });
            Assert.That(captured.IsCancellationRequested, Is.False, "A later server stop cannot cancel a completed request.");
        }

        [Test]
        public async Task ReusedConnectionCannotCancelOrReacquireTheCompletedRequest()
        {
            var firstClosed = Signal<(CancellationToken Token, IHttpContext Context)>();
            var secondEntered = Signal<CancellationToken>();
            var finish = Signal<bool>();
            await WithServer(async context =>
            {
                var token = context.GetRequestAbortedToken();
                if (context.Request.Url.AbsolutePath == "/first")
                {
                    context.Response.ContentLength64 = 0;
                    context.Response.Close();
                    firstClosed.TrySetResult((token, context));
                    await finish.Task;
                }
                else { secondEntered.TrySetResult(token); await Task.Delay(Timeout.Infinite, token); }
            }, async (port, _) =>
            {
                using var socket = await Request(port, "first");
                try
                {
                    var first = await firstClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var buffer = new byte[4096];
                    Assert.That(await socket.ReceiveAsync(buffer, SocketFlags.None), Is.GreaterThan(0));
                    var request = Encoding.ASCII.GetBytes("GET /second HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                    await socket.SendAsync(request, SocketFlags.None);
                    var second = await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(first.Context.GetRequestAbortedToken(), Is.EqualTo(first.Token));
                    Assert.That(() => first.Context.GetRequestAbortedToken(true), Throws.InvalidOperationException);
                    first.Context.GetImplementation().Close();
                    socket.LingerState = new LingerOption(true, 0);
                    socket.Dispose();
                    await Canceled(second);
                    Assert.That(first.Token.IsCancellationRequested, Is.False);
                }
                finally { finish.TrySetResult(true); }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ThrowingOrBlockingCallbacksCannotBlockServerStop(bool blocking)
        {
            var entered = Signal<CancellationToken>();
            var callbackEntered = Signal<bool>();
            using var release = new ManualResetEventSlim();
            var callbackDone = Signal<bool>();
            try
            {
                await WithServer(async context =>
                {
                    var token = context.GetRequestAbortedToken();
                    token.Register(() =>
                    {
                        callbackEntered.TrySetResult(true);
                        try { if (blocking) release.Wait(); else throw new InvalidOperationException("callback sentinel"); }
                        finally { callbackDone.TrySetResult(true); }
                    });
                    entered.TrySetResult(token);
                    // A non-cooperating handler is deliberately released independently below.
                    await Task.Run(() => release.Wait());
                }, async (port, stop) =>
                {
                    using var socket = await Request(port);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var stopping = Task.Run(stop.Cancel);
                    await stopping.WaitAsync(TimeSpan.FromSeconds(2));
                    await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    release.Set();
                });
                await callbackDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { release.Set(); }
        }
    }
}
