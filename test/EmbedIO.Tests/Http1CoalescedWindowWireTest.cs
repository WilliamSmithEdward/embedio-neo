using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class Http1CoalescedWindowWireTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task FlushReachesThePeerWhileItsInlineHandlerBlocks(bool asynchronousFlush)
        {
            var url =
                new UriBuilder(HttpsSmoke.GetUrl())
                {
                    Host = "127.0.0.1",
                    Scheme = "http"
                }.Uri;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(options => options.WithUrlPrefix(url.ToString()));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    context.Response.SendChunked = true;
                    var output = context.Response.OutputStream;
                    output.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);

                    if (asynchronousFlush)
                        output
                            .FlushAsync(context.CancellationToken)
                            .GetAwaiter()
                            .GetResult();

                    else output.Flush();

                    entered.TrySetResult();

                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The peer never released the inline handler.");

                    output.Write(Encoding.ASCII.GetBytes("DEF"), 0, 3);
                    return Task.CompletedTask;
                }));
            var running = server.RunAsync(timeout.Token);

            try
            {
                using var peer = new TcpClient();
                await peer.ConnectAsync(IPAddress.Loopback, url.Port, timeout.Token);
                var transport = peer.GetStream();
                await transport.WriteAsync(
                    Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {url.Authority}\r\nConnection: close\r\n\r\n"),
                    timeout.Token
                );
                await entered.Task.WaitAsync(timeout.Token);
                using var promptness = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                promptness.CancelAfter(TimeSpan.FromSeconds(2));
                var prefix = await ReadThroughAsync(transport, "3\r\nABC\r\n", promptness.Token);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(prefix, Does.StartWith("HTTP/1.1 200 OK\r\n"));
                    Assert.That(prefix, Does.EndWith("\r\n\r\n3\r\nABC\r\n"));
                    Assert.That(
                        release.IsSet,
                        Is.False,
                        "The held bytes reached the peer before the synchronous handler returned."
                    );
                }

                release.Set();
                using var remainder = new MemoryStream();
                await transport.CopyToAsync(remainder, timeout.Token);
                Assert.That(Encoding.ASCII.GetString(remainder.ToArray()), Is.EqualTo("3\r\nDEF\r\n0\r\n\r\n"));
            }

            finally
            {
                release.Set();
                timeout.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CoalescedResponsesKeepPipelinedAndSequentialRequestsInOrder(bool pipelined)
        {
            var url =
                new UriBuilder(HttpsSmoke.GetUrl())
                {
                    Host = "127.0.0.1",
                    Scheme = "http"
                }.Uri;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(options => options.WithUrlPrefix(url.ToString()));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    context.Response.SendChunked = true;
                    var output = context.Response.OutputStream;
                    var value = context.Request.Url.AbsolutePath == "/first" ? "AAA" : "BBB";
                    output.Write(Encoding.ASCII.GetBytes(value), 0, 3);
                    output.Flush();
                    output.Write(Encoding.ASCII.GetBytes(value), 0, 3);
                    return Task.CompletedTask;
                }));
            var running = server.RunAsync(timeout.Token);

            try
            {
                using var peer = new TcpClient();
                await peer.ConnectAsync(IPAddress.Loopback, url.Port, timeout.Token);
                var transport = peer.GetStream();
                var firstRequest = $"GET /first HTTP/1.1\r\nHost: {url.Authority}\r\n\r\n";
                var secondRequest = $"GET /second HTTP/1.1\r\nHost: {url.Authority}\r\nConnection: close\r\n\r\n";
                await transport.WriteAsync(
                    Encoding.ASCII.GetBytes(firstRequest + (pipelined ? secondRequest : "")),
                    timeout.Token
                );
                var first = await ReadThroughAsync(transport, "0\r\n\r\n", timeout.Token);

                if (!pipelined) await transport.WriteAsync(Encoding.ASCII.GetBytes(secondRequest), timeout.Token);

                using var remainder = new MemoryStream();
                await transport.CopyToAsync(remainder, timeout.Token);
                var second = Encoding.ASCII.GetString(remainder.ToArray());

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(first, Does.StartWith("HTTP/1.1 200 OK\r\n"));
                    Assert.That(Body(first), Is.EqualTo("3\r\nAAA\r\n3\r\nAAA\r\n0\r\n\r\n"));
                    Assert.That(second, Does.StartWith("HTTP/1.1 200 OK\r\n"));
                    Assert.That(Body(second), Is.EqualTo("3\r\nBBB\r\n3\r\nBBB\r\n0\r\n\r\n"));
                }
            }

            finally
            {
                timeout.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task AHandlerErrorAfterFlushPreservesCommittedOutputWithoutAReplacementResponse(
            bool asynchronousFlush,
            bool httpException
        )
        {
            var url =
                new UriBuilder(HttpsSmoke.GetUrl())
                {
                    Host = "127.0.0.1",
                    Scheme = "http"
                }.Uri;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(options => options.WithUrlPrefix(url.ToString()));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    if (context.RequestedPath == "/healthy")
                        return context.SendStringAsync("healthy", MimeType.PlainText, Encoding.UTF8);

                    context.Response.SendChunked = true;
                    var output = context.Response.OutputStream;
                    output.Write(Encoding.ASCII.GetBytes("before"), 0, 6);

                    if (asynchronousFlush)
                        output
                            .FlushAsync(context.CancellationToken)
                            .GetAwaiter()
                            .GetResult();

                    else output.Flush();

                    if (httpException) throw HttpException.BadRequest("controlled error after commitment");

                    throw new InvalidOperationException("controlled failure after commitment");
                }));
            var running = server.RunAsync(timeout.Token);

            try
            {
                using var peer = new TcpClient();
                await peer.ConnectAsync(IPAddress.Loopback, url.Port, timeout.Token);
                var transport = peer.GetStream();
                await transport.WriteAsync(
                    Encoding.ASCII.GetBytes(
                        $"GET /failure HTTP/1.1\r\nHost: {url.Authority}\r\nConnection: close\r\n\r\n"
                    ),
                    timeout.Token
                );
                using var bytes = new MemoryStream();

                try
                {
                    await transport.CopyToAsync(bytes, timeout.Token);
                }

                catch (IOException) { }

                var wire = Encoding.ASCII.GetString(bytes.ToArray());

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(wire, Does.StartWith("HTTP/1.1 200 OK\r\n"));
                    Assert.That(
                        Body(wire),
                        Is.EqualTo("6\r\nbefore\r\n"),
                        "A committed response must retain its chunk without a replacement 400/500 response or a successful final chunk."
                    );
                }

                using var client = new HttpClient();
                var healthy = await client.GetStringAsync(new Uri(url, "healthy"), timeout.Token);
                Assert.That(healthy, Is.EqualTo("healthy"));
            }

            finally
            {
                timeout.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static string Body(string wire)
        {
            var boundary = wire.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            if (boundary <= 0) throw new AssertionException("Missing complete response head.");

            return wire[(boundary + 4)..];
        }

        private static async Task<string> ReadThroughAsync(Stream transport, string ending, CancellationToken token)
        {
            // Reading one byte at a time keeps a following pipelined head in the transport.
            using var bytes = new MemoryStream();
            var next = new byte[1];

            while (true)
            {
                var read = await transport.ReadAsync(next, 0, next.Length, token);

                if (read == 0) throw new EndOfStreamException("The response ended before its expected boundary.");

                bytes.WriteByte(next[0]);
                var wire = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));

                if (wire.EndsWith(ending, StringComparison.Ordinal)) return wire;
            }
        }
    }
}
