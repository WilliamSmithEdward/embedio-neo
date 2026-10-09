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
    // Unresolved findings remain Explicit; enable each case with its verified fix.
    public class HttpConformanceFindingsTest
    {
        public enum ReadPath { CopyToAsync, ArrayReadAsync, MemoryReadAsync, SyncRead }

        private sealed class FatalObservedWebServer : WebServer
        {
            private int _fatalCalls;
            internal FatalObservedWebServer(Action<WebServerOptions> configure) : base(configure) { }
            internal int FatalCalls => Volatile.Read(ref _fatalCalls);
            protected override void OnFatalException()
            {
                Interlocked.Increment(ref _fatalCalls);
                base.OnFatalException();
            }
        }

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
        // Before correction this reaches the application as an exception and becomes 500.
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
        // the HTTP/3 listener. Before correction, closing the cancelled response threw InvalidOperationException
        // ("HTTP/3 response is no longer writable"); WebServerBase treated it as fatal and
        // OnFatalException disposes the listener, so later connections fail for every client.
        [Test]
        public async Task CancelledHttp3UploadsDoNotStopTheListener()
        {
            if (!System.Net.Quic.QuicListener.IsSupported || !System.Net.Quic.QuicConnection.IsSupported)
                Assert.Ignore("No native QUIC support on this host.");
            using var certificate = EmbedIO.PlatformTests.HttpsSmoke.CreateCertificate();
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var bound = udp.LocalEndPoint as System.Net.IPEndPoint ?? throw new AssertionException("Missing UDP endpoint.");
            var prefix = $"https://localhost:{bound.Port}/";
            udp.Dispose();
            using var monitored = new FatalObservedWebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate));
            var server = monitored.WithModule(new ActionModule("/echo", HttpVerbs.Post, async context =>
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
                    Assert.That(monitored.FatalCalls, Is.Zero, "A request failure invoked listener-wide fatal cleanup.");
                    Assert.That(server.Listener.IsListening, Is.True);
                    using var probe = Http3Client(certificate);
                    using var health = await probe.GetAsync(prefix + "plain");
                    Assert.That(await health.Content.ReadAsStringAsync(), Is.EqualTo("hello"), $"Listener stopped after {round} cancellation rounds.");
                }
            }
            catch (System.Net.Http.HttpRequestException error)
            {
                TestContext.Progress.WriteLine($"HTTP/3 cancellation probe failed with listener state {server.State}, accepting={server.Listener.IsListening}, fatal calls={monitored.FatalCalls}: {error.Message}");
                throw;
            }
            finally
            {
                stop.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (System.Net.HttpListenerException) { }
            }
        }

        // F4: RFC 9113 Sections 5.4.2 and 6.4. RST_STREAM is a stream error; the connection
        // must keep working. Http2Exchange writes with a token linked to the stream reset and
        // Http2FrameTransport marks its output failed on any exception, cancellation included,
        // so a reset that lands during a shared write closes the connection without GOAWAY.
        [Explicit("Finding F4: a client RST_STREAM during a response write can close the HTTP/2 connection without GOAWAY.")]
        [Test]
        public async Task ClientResetDoesNotCloseTheHttp2Connection()
        {
            var url = Resources.GetServerAddress();
            var port = new Uri(url).Port;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/stream", HttpVerbs.Get, async context =>
                {
                    using var output = context.OpenResponseStream(false, false);
                    var chunk = new byte[700];
                    for (var i = 0; i < 150; i++)
                    {
                        await output.WriteAsync(chunk, context.CancellationToken);
                        await output.FlushAsync(context.CancellationToken);
                    }
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                var random = new Random(1309);
                var lost = 0;
                const int trials = 500;
                for (var trial = 0; trial < trials; trial++)
                {
                    using var client = new TcpClient { NoDelay = true };
                    await client.ConnectAsync("localhost", port);
                    var stream = client.GetStream();
                    var head = Http2Frame(1, 0x5, 1, Literal(":method", "GET"), Literal(":scheme", "http"),
                        Literal(":authority", $"localhost:{port}"), Literal(":path", "/stream"));
                    await stream.WriteAsync(Combine(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), Http2Frame(4, 0, 0), head));
                    await Task.Delay(random.Next(0, 3));
                    var reset = Http2Frame(3, 0, 1, new byte[] { 0, 0, 0, 8 });
                    var ping = Http2Frame(6, 0, 0, Encoding.ASCII.GetBytes("embedio!"));
                    try
                    {
                        await stream.WriteAsync(Combine(reset, ping));
                        if (!await PingAcknowledgedAsync(stream)) lost++;
                    }
                    catch (IOException) { lost++; }
                }
                Assert.That(lost, Is.Zero, $"{lost} of {trials} connections closed after RST_STREAM(CANCEL).");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // HPACK literal header field without indexing, new name, no Huffman (RFC 7541 6.2.2).
        private static byte[] Literal(string name, string value)
        {
            var n = Encoding.ASCII.GetBytes(name);
            var v = Encoding.ASCII.GetBytes(value);
            if (n.Length > 126 || v.Length > 126) throw new ArgumentException("Test literals are limited to short strings.");
            return Combine(new byte[] { 0x00, (byte)n.Length }, n, new[] { (byte)v.Length }, v);
        }

        private static byte[] Http2Frame(byte type, byte flags, int stream, params byte[][] payloads)
        {
            var payload = Combine(payloads);
            var frame = new byte[9 + payload.Length];
            frame[0] = (byte)(payload.Length >> 16); frame[1] = (byte)(payload.Length >> 8); frame[2] = (byte)payload.Length;
            frame[3] = type; frame[4] = flags;
            frame[5] = (byte)(stream >> 24); frame[6] = (byte)(stream >> 16); frame[7] = (byte)(stream >> 8); frame[8] = (byte)stream;
            payload.CopyTo(frame, 9);
            return frame;
        }

        private static byte[] Combine(params byte[][] parts)
        {
            using var buffer = new MemoryStream();
            foreach (var part in parts) buffer.Write(part);
            return buffer.ToArray();
        }

        // Reads frames until a PING ACK arrives (true) or the connection ends or stalls (false).
        private static async Task<bool> PingAcknowledgedAsync(NetworkStream stream)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var header = new byte[9];
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header, timeout.Token);
                    var length = (header[0] << 16) | (header[1] << 8) | header[2];
                    var payload = new byte[length];
                    await stream.ReadExactlyAsync(payload, timeout.Token);
                    if (header[3] == 6 && (header[4] & 1) != 0) return true;
                }
            }
            catch (EndOfStreamException) { return false; }
            catch (OperationCanceledException) { return false; }
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
