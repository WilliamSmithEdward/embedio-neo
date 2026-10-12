using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #136, first increment: opt-in server-wide request body limits, bounded
    // concurrent dispatch with a bounded queue, and a cooperative request deadline.
    // Every case drives the real TCP engine; HTTP/2 variants use prior-knowledge h2c.
    public class Issue136_RequestLimits
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        // ---------------------------------------------------------------- body limits

        [TestCase(1)]
        [TestCase(2)]
        public async Task DeclaredLengthOverTheLimitIsRejectedBeforeAnyModuleRuns(int protocol)
        {
            var entered = 0;
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(100), context =>
            {
                _ = Interlocked.Increment(ref entered);
                return Host.EchoLength(context);
            }, HttpVerbs.Any);
            using var client = Host.CreateClient(protocol);
            using var response = await client.SendAsync(Post(client, host.Url, new ByteArrayContent(new byte[101])));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(entered, Is.Zero);
            if (protocol == 1) Assert.That(response.Headers.ConnectionClose, Is.True, "HTTP/1 does not drain an unread body it refused.");
            Assert.That(await host.EchoLengthAsync(client, 100), Is.EqualTo("100"), "A body at the limit is accepted afterwards.");
            Assert.That(entered, Is.EqualTo(1));
        }

        // HTTP/2 only: an HTTP/1 client still sending when the server answers 413 and closes
        // usually sees a connection reset instead of the response. The raw-socket chunked
        // case below covers HTTP/1.
        [Test]
        public async Task UnknownLengthBodyFailsWhenAReadCrossesTheLimit()
        {
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(1000), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(2);
            using (var over = await client.SendAsync(Post(client, host.Url, new StreamContent(new MemoryStream(new byte[5000])))))
            {
                Assert.That(over.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
                Assert.That(await over.Content.ReadAsStringAsync(), Does.Contain("configured byte limit"));
            }
            using var exact = await client.SendAsync(Post(client, host.Url, new StreamContent(new MemoryStream(new byte[1000]))));
            Assert.That(await exact.Content.ReadAsStringAsync(), Is.EqualTo("1000"), "Exactly the limit is accepted, also on a reused HTTP/2 connection.");
        }

        [Test]
        public async Task ChunkedOverLimitClosesTheHttp1ConnectionInsteadOfDrainingIt()
        {
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(64), Host.EchoLength, HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            var chunk = new string('x', 40);
            var request = $"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nTransfer-Encoding: chunked\r\n\r\n"
                + $"28\r\n{chunk}\r\n28\r\n{chunk}\r\n";
            await socket.SendAsync(request);
            // The client never finishes the body; the server must still answer and close.
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 413"));
            Assert.That(wire, Does.Contain("Connection: close"));
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task ZeroLimitAcceptsBodylessRequestsOnly()
        {
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(0), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(1);
            Assert.That(await client.GetStringAsync(host.Url), Is.EqualTo("0"));
            using var post = await client.SendAsync(Post(client, host.Url, new ByteArrayContent(new byte[1])));
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        }

        [Test]
        public async Task TransferredAndDecodedLimitsAreIndependent()
        {
            var payload = new byte[100_000];
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, true)) gzip.Write(payload);
                compressed = buffer.ToArray();
            }
            Assert.That(compressed.Length, Is.LessThan(1000));

            await using var host = Host.Start(o => o.WithSupportCompressedRequests(true).WithMaximumRequestBodyBytes(1000).WithMaximumDecompressedRequestBodyBytes(200_000),
                async context =>
                {
                    using var body = context.OpenRequestStream();
                    var total = 0L;
                    var chunk = new byte[8192];
                    int read;
                    while ((read = await body.ReadAsync(chunk, 0, chunk.Length)) > 0) total += read;
                    await context.SendStringAsync(total.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", WebServer.Utf8NoBomEncoding);
                }, HttpVerbs.Any);
            using var client = Host.CreateClient(1);
            var small = new ByteArrayContent(compressed);
            small.Headers.ContentEncoding.Add("gzip");
            using (var response = await client.SendAsync(Post(client, host.Url, small)))
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("100000"), "A small transfer may decode to more than the transferred limit.");

            // A declared over-limit HTTP/1 body is rejected before it is read. Do
            // not race HttpClient's upload against that intentional connection
            // close: unread uploaded bytes can turn the response into a TCP reset.
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 2000\r\n\r\n");
            var rejected = await socket.ReadToEndAsync();
            Assert.That(rejected, Does.StartWith("HTTP/1.1 413"));
            Assert.That(rejected, Does.Contain("Connection: close"));
            Assert.That(rejected, Does.Contain("Request body exceeds").And.Not.Contain("Decompressed"));
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        // ------------------------------------------------------------ admission

        [TestCase(1)]
        [TestCase(2)]
        public async Task RequestsBeyondTheSlotsAndQueueAreRejectedWith503(int protocol)
        {
            using var gate = new Gate(2);
            await using var host = Host.Start(o => o.WithMaximumConcurrentRequests(2), gate.HandleAsync);
            using var client = Host.CreateClient(protocol);
            var first = client.GetStringAsync(host.Url);
            var second = client.GetStringAsync(host.Url);
            await gate.Entered.WaitAsync(Deadline);
            using (var third = await client.GetAsync(host.Url))
            {
                Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                Assert.That(await third.Content.ReadAsStringAsync(), Does.Contain("maximum number of requests"));
            }
            Assert.That(gate.EnteredCount, Is.EqualTo(2), "The rejected request never reached a module.");
            gate.Release();
            Assert.That(await first.WaitAsync(Deadline), Is.EqualTo("released"));
            Assert.That(await second.WaitAsync(Deadline), Is.EqualTo("released"));
            // A client can observe a response before the server's completion code returns
            // the slot, so the next request is retried briefly rather than assumed to fit.
            Assert.That(await Eventually(client, host.Url), Is.EqualTo(HttpStatusCode.OK), "Slots return after their requests complete.");
        }

        private static async Task<HttpStatusCode> Eventually(HttpClient client, string url)
        {
            var stop = DateTime.UtcNow + Deadline;
            while (true)
            {
                using var response = await client.GetAsync(url);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable || DateTime.UtcNow > stop) return response.StatusCode;
                await Task.Delay(20);
            }
        }

        [Test]
        public async Task QueuedRequestWaitsForAFreeSlot()
        {
            using var gate = new Gate(1);
            await using var host = Host.Start(o => o.WithMaximumConcurrentRequests(1, queueLimit: 1), gate.HandleAsync);
            using var client = Host.CreateClient(1);
            var first = client.GetStringAsync(host.Url);
            await gate.Entered.WaitAsync(Deadline);
            var queued = client.GetStringAsync(host.Url);
            await Task.Delay(200);
            Assert.That(queued.IsCompleted, Is.False, "The second request waits in the queue.");
            using (var overflow = await client.GetAsync(host.Url))
                Assert.That(overflow.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "The queue holds one request.");
            Assert.That(gate.EnteredCount, Is.EqualTo(1));
            gate.Release();
            Assert.That(await first.WaitAsync(Deadline), Is.EqualTo("released"));
            Assert.That(await queued.WaitAsync(Deadline), Is.EqualTo("released"));
            Assert.That(gate.EnteredCount, Is.EqualTo(2));
        }

        [Test]
        public async Task FailingHandlersReturnTheirSlots()
        {
            // A queue of one absorbs the completion race; a leaked slot would make every later
            // request wait in the queue until the client times out.
            await using var host = Host.Start(o => o.WithMaximumConcurrentRequests(1, queueLimit: 1), context =>
                context.Request.Url.AbsolutePath == "/fail" ? throw new InvalidOperationException("handler failure")
                    : context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding));
            using var client = Host.CreateClient(1);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var failed = await client.GetAsync(host.Url + "fail");
                Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError), "attempt " + attempt);
            }
            Assert.That(await client.GetStringAsync(host.Url), Is.EqualTo("ok"));
        }

        [Test]
        public async Task LimitsBelongToOneServerInstance()
        {
            using var gate = new Gate(1);
            await using var limited = Host.Start(o => o.WithMaximumConcurrentRequests(1), gate.HandleAsync);
            await using var other = Host.Start(_ => { }, context => context.SendStringAsync("other", "text/plain", WebServer.Utf8NoBomEncoding));
            using var client = Host.CreateClient(1);
            var held = client.GetStringAsync(limited.Url);
            await gate.Entered.WaitAsync(Deadline);
            using (var rejected = await client.GetAsync(limited.Url))
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await client.GetStringAsync(other.Url), Is.EqualTo("other"), "Another server is not affected.");
            gate.Release();
            Assert.That(await held.WaitAsync(Deadline), Is.EqualTo("released"));
        }

        // ------------------------------------------------------------- deadlines

        [TestCase(1)]
        [TestCase(2)]
        public async Task DeadlineCancelsACooperativeHandlerAndAnswers503(int protocol)
        {
            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = Host.Start(o => o.WithRequestTimeout(TimeSpan.FromMilliseconds(300)), async context =>
            {
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                catch (OperationCanceledException) { observed.TrySetResult(true); throw; }
            });
            using var client = Host.CreateClient(protocol);
            using var response = await client.GetAsync(host.Url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("deadline"));
            Assert.That(await observed.Task.WaitAsync(Deadline), Is.True);
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task HandlerThatIgnoresTheDeadlineCompletesNormally()
        {
            await using var host = Host.Start(o => o.WithRequestTimeout(TimeSpan.FromMilliseconds(100)), async context =>
            {
                await Task.Delay(400);
                await context.SendStringAsync("late but complete", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            using var client = Host.CreateClient(1);
            Assert.That(await client.GetStringAsync(host.Url), Is.EqualTo("late but complete"),
                "Cancellation is cooperative, and an expired deadline does not abort the finished response.");
        }

        [Test]
        public async Task DeadlineAfterCommitmentEndsTheResponseVisibly()
        {
            await using var host = Host.Start(o => o.WithRequestTimeout(TimeSpan.FromMilliseconds(300)), async context =>
            {
                var bytes = Encoding.ASCII.GetBytes("partial");
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await context.Response.OutputStream.FlushAsync();
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            });
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"GET / HTTP/1.1\r\nHost: {host.Authority}\r\nConnection: close\r\n\r\n");
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire, Does.Contain("partial"));
            Assert.That(wire, Does.Not.EndWith("0\r\n\r\n"), "A committed response is not terminated as if complete.");
        }

        [Test]
        public async Task DeadlineCoversTimeWaitingInTheQueue()
        {
            // The held handler ignores its token, so its slot stays busy past the queued deadline.
            using var gate = new Gate(1, honorCancellation: false);
            await using var host = Host.Start(o => o.WithMaximumConcurrentRequests(1, queueLimit: 4).WithRequestTimeout(TimeSpan.FromMilliseconds(400)), gate.HandleAsync);
            using var client = Host.CreateClient(1);
            var held = client.GetAsync(host.Url);
            await gate.Entered.WaitAsync(Deadline);
            using (var queued = await client.GetAsync(host.Url))
            {
                Assert.That(queued.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                Assert.That(await queued.Content.ReadAsStringAsync(), Does.Contain("deadline"));
            }
            Assert.That(gate.EnteredCount, Is.EqualTo(1), "A request that times out while queued never reaches a module.");
            gate.Release();
            using var heldResponse = await held.WaitAsync(Deadline);
            Assert.That(heldResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), "The held handler ignored its deadline and completed.");
        }

        [Test]
        public async Task DeadlineInterruptsAStalledUploadRead()
        {
            await using var host = Host.Start(o => o.WithRequestTimeout(TimeSpan.FromMilliseconds(400)), async context =>
            {
                var buffer = new byte[100];
                var read = 0;
                while (read < 100)
                {
                    var count = await context.Request.InputStream.ReadAsync(buffer, read, buffer.Length - read, context.CancellationToken);
                    if (count == 0) break;
                    read += count;
                }
                await context.SendStringAsync(read.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", WebServer.Utf8NoBomEncoding);
            }, HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            // Declares 100 bytes and sends 10: a false Content-Length from a slow client.
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 100\r\n\r\n0123456789");
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 503"));
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        // ------------------------------------------------------------ configuration

        [Test]
        public void DefaultsPreserveExistingBehavior()
        {
            var options = new WebServerOptions();
            Assert.That(options.MaximumRequestBodyBytes, Is.Null);
            Assert.That(options.MaximumConcurrentRequests, Is.Null);
            Assert.That(options.RequestQueueLimit, Is.Zero);
            Assert.That(options.RequestTimeout, Is.Null);
        }

        [Test]
        public void InvalidValuesAreRejected()
        {
            var options = new WebServerOptions();
            Assert.That(() => options.MaximumRequestBodyBytes = -1, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.MaximumConcurrentRequests = 0, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.RequestQueueLimit = -1, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.RequestTimeout = TimeSpan.Zero, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.RequestTimeout = TimeSpan.FromDays(30), Throws.InstanceOf<ArgumentOutOfRangeException>());
            options.MaximumRequestBodyBytes = null;
            options.MaximumConcurrentRequests = null;
            options.RequestTimeout = null;
            Assert.That(options.MaximumRequestBodyBytes, Is.Null);
        }

        [Test]
        public void LimitsLockWithTheOptions()
        {
            var options = new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress())
                .WithMaximumRequestBodyBytes(10).WithMaximumConcurrentRequests(3, 5).WithRequestTimeout(TimeSpan.FromSeconds(2));
            Assert.That(options.MaximumConcurrentRequests, Is.EqualTo(3));
            Assert.That(options.RequestQueueLimit, Is.EqualTo(5));
            using var server = new WebServer(options);
            Assert.That(() => options.MaximumRequestBodyBytes = 20, Throws.InvalidOperationException);
            Assert.That(() => options.WithMaximumConcurrentRequests(4), Throws.InvalidOperationException);
            Assert.That(() => options.RequestTimeout = TimeSpan.FromSeconds(1), Throws.InvalidOperationException);
        }

        // ---------------------------------------------------------------- helpers

        private static HttpRequestMessage Post(HttpClient client, string url, HttpContent content) => new(HttpMethod.Post, url)
        {
            Content = content,
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
        };

        private sealed class Gate : IDisposable
        {
            private readonly int _expected;
            private readonly bool _honorCancellation;
            private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _count;
            public Gate(int expected, bool honorCancellation = true) { _expected = expected; _honorCancellation = honorCancellation; }
            public Task Entered => _entered.Task;
            public int EnteredCount => Volatile.Read(ref _count);
            public void Release() => _release.TrySetResult(true);
            public async Task HandleAsync(IHttpContext context)
            {
                if (context.Request.Url.AbsolutePath == "/probe") { await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding); return; }
                if (Interlocked.Increment(ref _count) >= _expected) _entered.TrySetResult(true);
                if (_honorCancellation) await _release.Task.WaitAsync(context.CancellationToken);
                else await _release.Task.WaitAsync(Deadline);
                await context.SendStringAsync("released", "text/plain", WebServer.Utf8NoBomEncoding);
            }
            public void Dispose() => _release.TrySetResult(true);
        }

        private sealed class RawSocket : IDisposable
        {
            private readonly TcpClient _client;
            public RawSocket(TcpClient client) { _client = client; }
            public Task SendAsync(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                return _client.GetStream().WriteAsync(bytes, 0, bytes.Length);
            }
            // Reads until the server closes; a reset after the response counts as the end.
            public async Task<string> ReadToEndAsync()
            {
                using var received = new MemoryStream();
                var buffer = new byte[4096];
                using var timeout = new CancellationTokenSource(Deadline);
                try
                {
                    int read;
                    while ((read = await _client.GetStream().ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0)
                        received.Write(buffer, 0, read);
                }
                catch (IOException) { }
                return Encoding.Latin1.GetString(received.ToArray());
            }
            public void Dispose() => _client.Dispose();
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;
            private Host(WebServer server, string url) { _server = server; Url = url; _running = server.RunAsync(_stop.Token); }
            public string Url { get; }
            public string Authority => new Uri(Url).Authority;

            public static Host Start(Action<WebServerOptions> configure, Func<IHttpContext, Task> handler, HttpVerbs verbs = HttpVerbs.Get)
            {
                var url = Resources.GetServerAddress();
                var server = new WebServer(o => configure(o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)))
                    .WithModule(new ActionModule("/", verbs, context => context.Request.Url.AbsolutePath == "/probe"
                        ? context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)
                        : handler(context)));
                server.OnHttpException = HttpExceptionHandler.PlainTextResponse;
                return new Host(server, url);
            }

            public static async Task EchoLength(IHttpContext context)
            {
                var total = 0L;
                var buffer = new byte[4096];
                int read;
                while ((read = await context.Request.InputStream.ReadAsync(buffer, 0, buffer.Length, context.CancellationToken)) > 0) total += read;
                await context.SendStringAsync(total.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", WebServer.Utf8NoBomEncoding);
            }

            public async Task<string> EchoLengthAsync(HttpClient client, int length)
            {
                using var response = await client.SendAsync(Post(client, Url, new ByteArrayContent(new byte[length])));
                return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            public static HttpClient CreateClient(int protocol) => new(new SocketsHttpHandler { UseProxy = false })
            {
                DefaultRequestVersion = protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = Deadline,
            };

            public async Task<RawSocket> ConnectAsync()
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, new Uri(Url).Port);
                return new RawSocket(client);
            }

            public async Task<string> ProbeAsync()
            {
                using var client = CreateClient(1);
                return await client.GetStringAsync(Url + "probe");
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(Deadline); }
                finally { _server.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
