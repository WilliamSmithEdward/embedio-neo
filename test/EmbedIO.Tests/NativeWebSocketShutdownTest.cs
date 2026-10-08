using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class NativeWebSocketShutdownTest
    {
        private sealed class PendingInitialization : WebSocketModule
        {
            internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal PendingInitialization() : base("/ws", false) { }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) => Task.CompletedTask;
            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Entered.TrySetResult(true);
                await Release.Task;
            }
        }

        [Test]
        public async Task NativePreCanceledAcceptDoesNotWaitForAConnection()
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.Microsoft));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            server.Listener.Start();
            var pending = server.Listener.GetContextAsync(canceled.Token);
            try
            {
                await Assert.ThatAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(2)),
                    Throws.InstanceOf<OperationCanceledException>());
                Assert.That(server.Listener.IsListening, Is.True);
            }
            finally
            {
                server.Listener.Stop();
                try { var context = await pending.WaitAsync(TimeSpan.FromSeconds(2)); context.Close(); }
                catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            }
        }

        [TestCase(HttpListenerMode.Microsoft)]
        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task CancellationDuringUpgradeReleasesAcceptAndAllConnectedTransports(HttpListenerMode mode)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            for (var round = 0; round < 32; round++)
            {
                var module = new PendingInitialization();
                using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
                using var stop = new CancellationTokenSource();
                var running = server.RunAsync(stop.Token);
                var clients = new TcpClient[4];
                var exchanges = new List<Task>(4);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    for (var index = 0; index < clients.Length; index++)
                    {
                        var client = new TcpClient();
                        clients[index] = client;
                        await client.ConnectAsync(IPAddress.Loopback, new Uri(url).Port, timeout.Token);
                        var stream = client.GetStream();
                        var prefix = $"GET /ws HTTP/1.1\r\nHost: {new Uri(url).Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(prefix), timeout.Token);
                        exchanges.Add(FinishUpgrade(stream, release.Task, timeout.Token));
                    }
                    release.TrySetResult(true);
                    if ((round & 3) == 1) await module.Entered.Task.WaitAsync(timeout.Token);
                    else if ((round & 1) == 0) await Task.Yield();
                    stop.Cancel();
                    await running.WaitAsync(timeout.Token);
                    await Task.WhenAll(exchanges).WaitAsync(timeout.Token);
                    Assert.That(server.Listener.IsListening, Is.False, $"Round {round}");
                }
                catch (Exception error)
                {
                    TestContext.Error.WriteLine($"Upgrade cancellation before cleanup: mode={mode}, round={round}, server={running.Status}, clients={exchanges.Count}. {error}");
                    throw;
                }
                finally
                {
                    release.TrySetResult(true);
                    module.Release.TrySetResult(true);
                    stop.Cancel();
                    try { await Task.WhenAll(exchanges).WaitAsync(timeout.Token); }
                    finally { foreach (var client in clients) client?.Dispose(); }
                    await running.WaitAsync(timeout.Token);
                }
            }
        }

        [TestCase(SocketError.OperationAborted, false)]
        [TestCase(SocketError.OperationAborted, true)]
        [TestCase(SocketError.ConnectionReset, false)]
        [TestCase(SocketError.ConnectionReset, true)]
        [TestCase(SocketError.Success, false)]
        public async Task StoppedEndpointDisposesSocketFromAcceptCompletion(SocketError result, bool runtimeClearedSocket)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var listener = new Net.HttpListener();
            listener.AddPrefix(url);
            listener.Start();
            var registrations = (IDictionary)(typeof(Net.EndPointManager).GetField("Registrations", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                ?? throw new AssertionException("Missing endpoint registrations."));
            var prefixes = (IDictionary)(registrations[listener] ?? throw new AssertionException("Missing listener registration."));
            var endpoints = (IEnumerable)(prefixes[url] ?? throw new AssertionException("Missing endpoint."));
            var endpoint = endpoints.Cast<object>().Single();
            listener.Stop();

            // Inject a completed accept that still owns a real connected socket.
            // Windows can report an aborted completion after TCP has connected.
            using var peerListener = new TcpListener(IPAddress.Loopback, 0);
            peerListener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            var accepting = peerListener.AcceptSocketAsync(deadline.Token);
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)peerListener.LocalEndpoint).Port, deadline.Token);
            using var accepted = await accepting;
            var argsType = endpoint.GetType().GetNestedType("AcceptEventArgs", BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing owned accept arguments.");
            using var args = (SocketAsyncEventArgs)(Activator.CreateInstance(argsType, true)
                ?? throw new AssertionException("Cannot create accept arguments."));
            args.UserToken = endpoint;
            args.AcceptSocket = runtimeClearedSocket ? null : accepted;
            args.SocketError = result;
            (argsType.GetField("PendingSocket", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing pending accept socket.")).SetValue(args, accepted);
            var complete = endpoint.GetType().GetMethod("ProcessAccept", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new AssertionException("Missing accept completion handler.");
            complete.Invoke(null, new object[] { args });
            Assert.That(accepted.SafeHandle.IsClosed, Is.True, "Every completed accept socket must be released when its endpoint is stopped.");
            Assert.That(await client.GetStream().ReadAsync(new byte[1], deadline.Token), Is.Zero);
        }

        private static async Task FinishUpgrade(NetworkStream stream, Task release, CancellationToken timeout)
        {
            await release;
            try
            {
                await stream.WriteAsync(new byte[] { 13, 10 }, timeout);
                var buffer = new byte[1024];
                var received = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer, timeout)) != 0)
                {
                    received += count;
                    Assert.That(received, Is.LessThan(32768));
                }
            }
            catch (IOException error) when (error.InnerException is SocketException socketError
                && socketError.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown)
            {
                // A listener stop may abort either a partial HTTP request or an
                // upgraded transport. Deadline cancellation must still fail the test.
            }
        }

        [Test]
        public async Task ShutdownDoesNotSerializeAnotherHttpResponseAfterUpgrade()
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var module = new PendingInitialization();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.Microsoft)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, new Uri(url).Port, timeout.Token);
                var stream = client.GetStream();
                var request = $"GET /ws HTTP/1.1\r\nHost: {new Uri(url).Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
                var headers = new StringBuilder();
                var single = new byte[1];
                while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.That(headers.Length, Is.LessThan(16384));
                    await stream.ReadExactlyAsync(single, timeout.Token);
                    headers.Append((char)single[0]);
                }
                Assert.That(headers.ToString(), Does.StartWith("HTTP/1.1 101"));
                await module.Entered.Task.WaitAsync(timeout.Token);
                stop.Cancel();
                await running.WaitAsync(timeout.Token);
                using var trailing = new MemoryStream();
                try { await stream.CopyToAsync(trailing, timeout.Token); }
                catch (IOException error) when (OperatingSystem.IsWindows()
                    && error.InnerException is SocketException socketError
                    && socketError.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // HTTP.sys aborts upgraded transports on Stop. Any bytes read
                    // before that reset must still satisfy the wire assertion.
                }
                Assert.That(Encoding.ASCII.GetString(trailing.ToArray()), Is.Empty,
                    "A stopped upgraded transport must not append an HTTP response to the WebSocket wire.");
            }
            finally
            {
                module.Release.TrySetResult(true);
                stop.Cancel();
                await running.WaitAsync(timeout.Token);
            }
        }
    }
}
