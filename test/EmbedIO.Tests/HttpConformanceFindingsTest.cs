using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Deterministic reproductions of findings from test/EmbedIO.Conformance (#181).
    // Cases marked Explicit fail on the current engine; remove Explicit with the fix.
    public class HttpConformanceFindingsTest
    {
        public enum ReadPath { CopyToAsync, ArrayReadAsync, MemoryReadAsync, SyncRead }

        // F1: RFC 9112 Section 8. A Content-Length body cut short by EOF is incomplete;
        // the application must not observe it as a complete, shorter body.
        [Explicit("Finding F1: RequestStream reports EOF instead of an error when the peer closes before Content-Length bytes arrive.")]
        [TestCase(ReadPath.CopyToAsync)]
        [TestCase(ReadPath.ArrayReadAsync)]
        [TestCase(ReadPath.MemoryReadAsync)]
        [TestCase(ReadPath.SyncRead)]
        public async Task TruncatedContentLengthBodyIsNotDeliveredAsComplete(ReadPath path)
        {
            var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/echo", HttpVerbs.Post, async context =>
                {
                    try
                    {
                        var length = await ReadAllAsync(context.Request.InputStream, path);
                        observed.TrySetResult("completed with " + length + " bytes");
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException)
                    {
                        observed.TrySetResult("failed: " + error.GetType().Name);
                        throw;
                    }
                    await context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                var port = new Uri(url).Port;
                using var client = new TcpClient();
                await client.ConnectAsync("localhost", port);
                var stream = client.GetStream();
                var request = Encoding.ASCII.GetBytes($"POST /echo HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: 10\r\n\r\nabc");
                await stream.WriteAsync(request);
                client.Client.Shutdown(SocketShutdown.Send);
                var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(result, Does.StartWith("failed"), "The handler treated a 3-of-10-byte body as complete.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // F2: RFC 9112 Sections 2.2 and 7.1. Invalid chunk framing is a client error.
        // It currently reaches the application as an exception and becomes 500.
        [Explicit("Finding F2: malformed chunked bodies detected during application reads produce 500 instead of 400.")]
        [TestCase("FFFFFFFFFFFFFFFFFF\r\nabc\r\n0\r\n\r\n")]
        [TestCase("3\r\nabcX0\r\n\r\n")]
        [TestCase(" 3\r\nabc\r\n0\r\n\r\n")]
        public async Task MalformedChunkedBodyIsAClientError(string body)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/echo", HttpVerbs.Post, async context =>
                {
                    await ReadAllAsync(context.Request.InputStream, ReadPath.CopyToAsync);
                    await context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                var port = new Uri(url).Port;
                using var client = new TcpClient();
                await client.ConnectAsync("localhost", port);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /echo HTTP/1.1\r\nHost: localhost:{port}\r\nTransfer-Encoding: chunked\r\n\r\n{body}"));
                var buffer = new byte[256];
                using var reading = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var read = await stream.ReadAsync(buffer, reading.Token);
                var statusLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];
                Assert.That(statusLine, Is.EqualTo("HTTP/1.1 400 Bad Request"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // F3: RFC 9114 Section 4.1.1. A client cancelling its own upload must not stop
        // the HTTP/3 listener. Closing the cancelled response throws InvalidOperationException
        // ("HTTP/3 response is no longer writable"); WebServerBase treats it as fatal and
        // OnFatalException disposes the listener, so later connections fail for every client.
        [Explicit("Finding F3: a cancelled HTTP/3 upload can escalate to a fatal exception that disposes the listener.")]
        [Test]
        public async Task CancelledHttp3UploadsDoNotStopTheListener()
        {
            if (!System.Net.Quic.QuicListener.IsSupported || !System.Net.Quic.QuicConnection.IsSupported)
                Assert.Ignore("No native QUIC support on this host.");
            using var certificate = EmbedIO.PlatformTests.HttpsSmoke.CreateCertificate();
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var prefix = $"https://localhost:{((System.Net.IPEndPoint)udp.LocalEndPoint!).Port}/";
            udp.Dispose();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule("/echo", HttpVerbs.Post, async context =>
                {
                    using var sink = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(sink, context.CancellationToken);
                    await context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                }))
                .WithModule(new ActionModule("/plain", HttpVerbs.Get, context => context.SendStringAsync("hello", "text/plain", Encoding.UTF8)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                var random = new Random(4441);
                for (var round = 1; round <= 100; round++)
                {
                    using (var client = Http3Client(certificate))
                    {
                        // Warm the connection so cancellation lands mid-upload, not mid-handshake.
                        using (var warm = await client.GetAsync(prefix + "plain")) warm.EnsureSuccessStatusCode();
                        var uploads = new Task[4];
                        for (var i = 0; i < uploads.Length; i++)
                            uploads[i] = SendCancelledAsync(client, prefix + "echo", random.Next(0, 30));
                        await Task.WhenAll(uploads);
                    }
                    if (round % 10 != 0) continue;
                    using var probe = Http3Client(certificate);
                    using var health = await probe.GetAsync(prefix + "plain");
                    Assert.That(await health.Content.ReadAsStringAsync(), Is.EqualTo("hello"), $"Listener stopped after {round} cancellation rounds.");
                }
            }
            finally
            {
                stop.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (System.Net.HttpListenerException) { }
            }
        }

        private static System.Net.Http.HttpClient Http3Client(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => new(new System.Net.Http.SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString(),
            },
        })
        {
            DefaultRequestVersion = System.Net.HttpVersion.Version30,
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(15),
        };

        // Streams a large body and cancels the request once its first chunk is written.
        private static async Task SendCancelledAsync(System.Net.Http.HttpClient client, string url, int delayMs)
        {
            using var cancel = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var content = new SlowContent(started);
            var sending = client.PostAsync(url, content, cancel.Token);
            await Task.WhenAny(started.Task, sending);
            await Task.Delay(delayMs);
            cancel.Cancel();
            try { using var response = await sending; }
            catch (OperationCanceledException) { }
            catch (System.Net.Http.HttpRequestException) { }
        }

        private sealed class SlowContent(TaskCompletionSource started) : System.Net.Http.HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
            {
                var chunk = new byte[16384];
                for (var i = 0; i < 1000; i++)
                {
                    await stream.WriteAsync(chunk, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    started.TrySetResult();
                    await Task.Delay(1, cancellationToken);
                }
            }

            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
                => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        private static async Task<long> ReadAllAsync(Stream input, ReadPath path)
        {
            var buffer = new byte[4096];
            long total = 0;
            switch (path)
            {
                case ReadPath.CopyToAsync:
                    using (var copy = new MemoryStream())
                    {
                        await input.CopyToAsync(copy);
                        return copy.Length;
                    }
                case ReadPath.ArrayReadAsync:
                    for (int read; (read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0;) total += read;
                    return total;
                case ReadPath.MemoryReadAsync:
                    for (int read; (read = await input.ReadAsync(buffer.AsMemory())) > 0;) total += read;
                    return total;
                default:
                    for (int read; (read = input.Read(buffer, 0, buffer.Length)) > 0;) total += read;
                    return total;
            }
        }
    }
}
