using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue457_ProxyDisconnects
    {
        [TestCase(HttpListenerMode.EmbedIO, false, false, false)]
        [TestCase(HttpListenerMode.EmbedIO, false, false, true)]
        [TestCase(HttpListenerMode.EmbedIO, false, true, false)]
        [TestCase(HttpListenerMode.EmbedIO, false, true, true)]
        [TestCase(HttpListenerMode.EmbedIO, true, false, false)]
        [TestCase(HttpListenerMode.EmbedIO, true, false, true)]
        [TestCase(HttpListenerMode.EmbedIO, true, true, false)]
        [TestCase(HttpListenerMode.EmbedIO, true, true, true)]
        [TestCase(HttpListenerMode.Microsoft, false, false, false)]
        [TestCase(HttpListenerMode.Microsoft, false, false, true)]
        [TestCase(HttpListenerMode.Microsoft, false, true, false)]
        [TestCase(HttpListenerMode.Microsoft, false, true, true)]
        [TestCase(HttpListenerMode.Microsoft, true, false, false)]
        [TestCase(HttpListenerMode.Microsoft, true, false, true)]
        [TestCase(HttpListenerMode.Microsoft, true, true, false)]
        [TestCase(HttpListenerMode.Microsoft, true, true, true)]
        public async Task ProxyDisconnectionHonorsWritePolicyAndLeavesServerHealthy(HttpListenerMode mode, bool ignoreWrites, bool gzip, bool graceful)
        {
            var root = Resources.GetServerAddress().Replace("localhost", "127.0.0.1");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeCount = 0;
            string? contentEncoding = null;
            Exception? failure = null;
            var writes = 0;
            CancellationToken handlerToken = default;
            using var server = new WebServer(o => o.WithUrlPrefix(root).WithMode(mode))
                .OnAny(async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/health")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    handlerToken = context.CancellationToken;
                    context.OnClose(_ => { Interlocked.Increment(ref closeCount); closed.TrySetResult(); });
                    try
                    {
                        context.Response.ContentType = "application/octet-stream";
                        using var output = context.OpenResponseStream(buffered: false, preferCompression: gzip);
                        contentEncoding = context.Response.Headers[HttpResponseHeader.ContentEncoding];
                        var frame = new byte[512];
                        new Random(457).NextBytes(frame);
                        await output.WriteAsync(frame, context.CancellationToken);
                        await output.FlushAsync(context.CancellationToken);
                        ready.TrySetResult();
                        await resume.Task.WaitAsync(context.CancellationToken);
                        for (var attempt = 0; attempt < 64; attempt++)
                        {
                            await output.WriteAsync(frame, context.CancellationToken);
                            await output.FlushAsync(context.CancellationToken);
                            writes++;
                            await Task.Delay(10, context.CancellationToken);
                        }
                    }
                    catch (Exception error) { failure = error; }
                    finally { finished.TrySetResult(); }
                });
            server.Listener.IgnoreWriteExceptions = ignoreWrites;
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            await using var proxy = new Proxy(new Uri(root));
            using var downstream = new TcpClient();
            try
            {
                await downstream.ConnectAsync(IPAddress.Loopback, proxy.Port);
                var encoding = gzip ? "Accept-Encoding: gzip\r\n" : "";
                await downstream.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                    $"GET /poll HTTP/1.1\r\nHost: {new Uri(root).Authority}\r\n{encoding}\r\n"));
                var status = new byte[12];
                await downstream.GetStream().ReadExactlyAsync(status).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(Encoding.ASCII.GetString(status), Is.EqualTo("HTTP/1.1 200"));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (gzip) Assert.That(contentEncoding, Is.EqualTo("gzip"));
                else Assert.That(contentEncoding, Is.Not.EqualTo("gzip"));
                await proxy.CloseUpstream(graceful);
                resume.TrySetResult();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(closeCount, Is.EqualTo(1));
                Assert.That(handlerToken, Is.EqualTo(stop.Token));
                Assert.That(handlerToken.IsCancellationRequested, Is.False);
                if (failure != null)
                    Assert.That(failure, Is.InstanceOf<IOException>().Or.InstanceOf<HttpListenerException>().Or.InstanceOf<ObjectDisposedException>());
                if (!ignoreWrites)
                    Assert.That(failure, Is.Not.Null);
                else if (mode == HttpListenerMode.EmbedIO)
                {
                    Assert.That(failure, Is.Null, "Managed transport errors remain suppressed by explicit policy.");
                    Assert.That(writes, Is.EqualTo(64));
                }

                using var healthy = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                Assert.That(await healthy.GetStringAsync(root + "health"), Is.EqualTo("healthy"));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                resume.TrySetResult();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task BufferedWritesRemainMemoryOperationsUntilResponseCommit(HttpListenerMode mode, bool ignoreWrites)
        {
            var root = Resources.GetServerAddress().Replace("localhost", "127.0.0.1");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? failure = null;
            var bufferedWritesSucceeded = false;
            using var server = new WebServer(o => o.WithUrlPrefix(root).WithMode(mode))
                .OnAny(async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/health")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    context.OnClose(_ => closed.TrySetResult());
                    try
                    {
                        using var output = context.OpenResponseStream(buffered: true, preferCompression: false);
                        await output.WriteAsync(new byte[512], context.CancellationToken);
                        ready.TrySetResult();
                        await resume.Task.WaitAsync(context.CancellationToken);
                        await output.WriteAsync(new byte[1024 * 1024], context.CancellationToken);
                        await output.FlushAsync(context.CancellationToken);
                        bufferedWritesSucceeded = true;
                    }
                    catch (Exception error) { failure = error; }
                    finally { finished.TrySetResult(); }
                });
            server.Listener.IgnoreWriteExceptions = ignoreWrites;
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            await using var proxy = new Proxy(new Uri(root));
            using var downstream = new TcpClient();
            try
            {
                await downstream.ConnectAsync(IPAddress.Loopback, proxy.Port);
                await downstream.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                    $"GET /poll HTTP/1.1\r\nHost: {new Uri(root).Authority}\r\n\r\n"));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await proxy.CloseUpstream(false);
                resume.TrySetResult();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(bufferedWritesSucceeded, Is.True, "Buffered Write/Flush must not be presented as transport observations.");
                if (failure != null)
                    Assert.That(failure, Is.InstanceOf<IOException>().Or.InstanceOf<HttpListenerException>().Or.InstanceOf<ObjectDisposedException>());
                if (!ignoreWrites) Assert.That(failure, Is.Not.Null, "Strict transport failure appears when buffered output is committed.");
                else if (mode == HttpListenerMode.EmbedIO) Assert.That(failure, Is.Null);
                using var healthy = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                Assert.That(await healthy.GetStringAsync(root + "health"), Is.EqualTo("healthy"));
                Assert.That(stop.IsCancellationRequested, Is.False);
            }
            finally
            {
                resume.TrySetResult();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private sealed class Proxy : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly TcpClient _upstream = new();
            private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenSource _stopping = new();
            private readonly Task _relaying;
            private TcpClient? _downstream;

            public Proxy(Uri target)
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _relaying = Relay(target);
            }

            public int Port { get; }

            public async Task CloseUpstream(bool graceful)
            {
                await _connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (graceful) _upstream.Client.Shutdown(SocketShutdown.Both);
                else _upstream.Client.LingerState = new LingerOption(true, 0);
                _upstream.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                _stopping.Cancel();
                _listener.Stop();
                _downstream?.Dispose();
                _upstream.Dispose();
                try { await _relaying.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
                finally { _stopping.Dispose(); }
            }

            private async Task Relay(Uri target)
            {
                _downstream = await _listener.AcceptTcpClientAsync(_stopping.Token);
                await _upstream.ConnectAsync(IPAddress.Loopback, target.Port, _stopping.Token);
                var incoming = _downstream.GetStream();
                var outgoing = _upstream.GetStream();
                _connected.TrySetResult();
                await Task.WhenAll(incoming.CopyToAsync(outgoing, _stopping.Token), outgoing.CopyToAsync(incoming, _stopping.Token));
            }
        }
    }
}
