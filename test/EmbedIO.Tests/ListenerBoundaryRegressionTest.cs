using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ListenerBoundaryRegressionTest
    {
        [TestCaseSource(nameof(FramingCases))]
        public async Task FixedLengthBodiesAndSequentialRequestsPreserveBoundaries(bool secure, int fragment, string lengths, int length)
        {
            using var fixture = new RawListener(secure);
            await fixture.Connect();
            for (var request = 0; request < 2; request++)
            {
                var accept = fixture.Listener.GetContextAsync(fixture.Token);
                var headers = request == 0 ? lengths : "Content-Length: 6\r\n";
                var body = request == 0 ? "abcdef".Substring(0, length) : "second";
                await fixture.Write($"POST /{request} HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Value: {new string('a', 256)}\r\n{headers}\r\n{body}", fragment);
                var context = await accept;
                using var received = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(received, fixture.Token);
                Assert.That(Encoding.ASCII.GetString(received.ToArray()), Is.EqualTo(body));
                Assert.That(context.Request.RawTarget, Is.EqualTo($"/{request}"));
                Assert.That(context.Request.ContentLength64, Is.EqualTo(body.Length));
                Respond(context);
                Assert.That(await fixture.ReadHeaders(), Does.StartWith("HTTP/1.1 204 "));
            }
        }

        [TestCase(false, "100-continue")]
        [TestCase(true, "100-continue")]
        [TestCase(false, "100-continue, 100-continue")]
        [TestCase(true, "100-continue, 100-continue")]
        [TestCase(false, " , 100-CoNtInUe , ")]
        [TestCase(true, " , 100-CoNtInUe , ")]
        [TestCase(false, "custom=\"x,100-continue,y\", 100-continue")]
        [TestCase(true, "custom=\"x,100-continue,y\", 100-continue")]
        public async Task ExpectContinuePrecedesBodyAndAllowsNextRequest(bool secure, string expectation)
        {
            using var fixture = new RawListener(secure);
            await fixture.Connect();
            var accept = fixture.Listener.GetContextAsync(fixture.Token);
            await fixture.Write("POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 6\r\nExpect: " + expectation + "\r\n\r\n", 7);
            Assert.That(await fixture.ReadHeaders(), Is.EqualTo("HTTP/1.1 100 Continue\r\n\r\n"));
            await fixture.Write("abcdef", 1);
            var context = await accept;
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body, fixture.Token);
            Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("abcdef"));
            Respond(context);
            Assert.That(await fixture.ReadHeaders(), Does.StartWith("HTTP/1.1 204 "));
            accept = fixture.Listener.GetContextAsync(fixture.Token);
            await fixture.Write("GET /next HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", 7);
            context = await accept;
            Assert.That(context.Request.RawTarget, Is.EqualTo("/next"));
            Respond(context, false);
            Assert.That(await fixture.ReadHeaders(), Does.StartWith("HTTP/1.1 204 "));
        }

        [TestCase(false, "POST", "1.0", "abcdef")]
        [TestCase(true, "POST", "1.0", "abcdef")]
        [TestCase(false, "GET", "1.1", "")]
        [TestCase(true, "GET", "1.1", "")]
        [TestCase(false, "POST", "1.1", "")]
        [TestCase(true, "POST", "1.1", "")]
        [TestCase(false, "PUT", "1.1", "")]
        [TestCase(true, "PUT", "1.1", "")]
        public async Task Http10AndBodylessRequestsDoNotSendContinue(bool secure, string method, string version, string payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            using var fixture = new RawListener(secure);
            await fixture.Connect();
            var accept = fixture.Listener.GetContextAsync(fixture.Token);
            await fixture.Write($"{method} / HTTP/{version}\r\nHost: 127.0.0.1\r\nContent-Length: {payload.Length}\r\nExpect: 100-continue\r\n\r\n{payload}", 7);
            var context = await accept;
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body, fixture.Token);
            Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo(payload));
            Respond(context, false);
            var response = await fixture.ReadHeaders();
            Assert.That(response, Does.Contain(" 204 "));
            Assert.That(response, Does.Not.Contain("100 Continue"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TruncatedBodyTerminatesReadAndListenerStillServes(bool secure)
        {
            using var fixture = new RawListener(secure);
            await fixture.Connect();
            var accept = fixture.Listener.GetContextAsync(fixture.Token);
            await fixture.Write("POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 6\r\n\r\nab", 8192);
            var context = await accept;
            fixture.Client.Client.Shutdown(SocketShutdown.Send);
            using var body = new MemoryStream();
            var failure = await Assert.CatchAsync<IOException>(async () => await context.Request.InputStream.CopyToAsync(body, fixture.Token));
            if (!secure) Assert.That(failure, Is.TypeOf<EndOfStreamException>());
            Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("ab"));
            // Partial bytes remain observable, but cannot represent a completed body.
            context.Response.StatusCode = 400;
            context.Response.ContentLength64 = 0;
            context.Response.KeepAlive = false;
            context.Close();
            await fixture.AssertHealthy();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NegativeLengthClosesConnectionWithoutDispatch(bool secure)
        {
            using var fixture = new RawListener(secure);
            await fixture.Connect();
            await fixture.Write("POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: -1\r\n\r\n", 7);
            using var rejected = new MemoryStream();
            await fixture.Stream.CopyToAsync(rejected, fixture.Token);
            Assert.That(Encoding.ASCII.GetString(rejected.ToArray()),
                Is.EqualTo("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            await fixture.AssertHealthy();
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task AcceptBurstsAndResetsAllowShutdownAndRestart(bool secure, bool reset)
        {
            using var fixture = new RawListener(secure);
            if (reset)
            {
                for (var i = 0; i < 32; i++)
                {
                    using var peer = new TcpClient();
                    await peer.ConnectAsync("127.0.0.1", fixture.Port, fixture.Token);
                    peer.Client.LingerState = new LingerOption(true, 0);
                }
            }
            using var client = secure ? HttpsSmoke.CreateClient((fixture.Certificate ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))) : new HttpClient();
            var serving = Task.Run(async () =>
            {
                for (var i = 0; i < 64; i++)
                {
                    var context = await fixture.Listener.GetContextAsync(fixture.Token);
                    Respond(context, false);
                }
            });
            await Task.WhenAll(Enumerable.Range(0, 64).Select(async _ =>
            {
                using var response = await client.GetAsync(fixture.Url, fixture.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }));
            await serving;
            var pending = fixture.Listener.GetContextAsync(fixture.Token);
            fixture.Listener.Stop();
            await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<System.Net.HttpListenerException>());
            fixture.Listener.Start();
            await fixture.AssertHealthy();
        }

        [TestCase(32)]
        [TestCase(128)]
        public async Task AlreadyQueuedConnectionsDrainAndPendingAcceptSurvives(int count)
        {
            using var fixture = new RawListener(false);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Listen(500);
            var port = ((IPEndPoint)(socket.LocalEndPoint ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))).Port;
            var endpointType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.EndPointListener", true);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var endpoint = RuntimeHelpers.GetUninitializedObject((endpointType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")));
            ((endpointType).GetField("<Listener>k__BackingField", fields) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(endpoint, fixture.Listener);
            ((endpointType).GetField("_sock", fields) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(endpoint, socket);
            ((endpointType).GetField("_endpoint", fields) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(endpoint, socket.LocalEndPoint);
            foreach (var name in new[] { "_routes", "_unregistered" })
            {
                var field = endpointType.GetField(name, fields);
                (field ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(endpoint, Activator.CreateInstance(field.FieldType));
            }
            var prefixType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.ListenerPrefix", true);
            ((endpointType).GetMethod("AddPrefix") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(endpoint,
                new[] { Activator.CreateInstance((prefixType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), $"http://127.0.0.1:{port}/"), fixture.Listener });
            var clients = new List<TcpClient>();
            var actorType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.TcpAcceptLoop")
                ?? throw new AssertionException("Missing owned TCP accept loop.");
            var admission = (Action<Socket>)(endpointType.GetMethod("ProcessAcceptedSocket", fields)
                ?? throw new AssertionException("Missing endpoint admission.")).CreateDelegate(typeof(Action<Socket>), endpoint);
            Func<bool> stopped = () => socket.SafeHandle.IsClosed;
            var actor = Activator.CreateInstance(actorType, fields, null,
                new object[] { socket, admission, stopped, (Action)socket.Dispose }, null)
                ?? throw new AssertionException("Missing accept actor instance.");
            var run = actorType.GetMethod("RunAsync", fields) ?? throw new AssertionException("Missing actor runner.");
            Task? running = null;
            try
            {
                // No accept is armed until every peer is in the socket backlog.
                // The BCL still chooses inline versus pending completion.
                for (var i = 0; i < count; i++)
                {
                    var peer = new TcpClient { NoDelay = true };
                    clients.Add(peer);
                    await peer.ConnectAsync(IPAddress.Loopback, port, fixture.Token);
                    await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /{i} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"), fixture.Token);
                }
                running = Task.Run(() => (Task)(run.Invoke(actor, null)
                    ?? throw new AssertionException("Missing accept task.")));
                var paths = new HashSet<string>();
                for (var i = 0; i < count; i++)
                {
                    var context = await fixture.Listener.GetContextAsync(fixture.Token);
                    Assert.That(paths.Add(context.Request.RawTarget), Is.True);
                    Respond(context, false);
                }
                Assert.That(paths, Is.EquivalentTo(Enumerable.Range(0, count).Select(i => $"/{i}")));
                using var late = new TcpClient();
                await late.ConnectAsync(IPAddress.Loopback, port, fixture.Token);
                await late.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /late HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"), fixture.Token);
                var last = await fixture.Listener.GetContextAsync(fixture.Token);
                Assert.That(last.Request.RawTarget, Is.EqualTo("/late"));
                Respond(last, false);
            }
            finally
            {
                ((IDisposable)endpoint).Dispose();
                if (running != null) await running.WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var peer in clients) peer.Dispose();
            }
        }

        private static IEnumerable<TestCaseData> FramingCases()
        {
            foreach (var secure in new[] { false, true })
                foreach (var fragment in new[] { 1, 7, 8192 })
                    foreach (var (name, headers, length) in new[]
                    {
                ("single", "Content-Length: 6\r\n", 6),
                ("equal-duplicates", "Content-Length: 6\r\nContent-Length: 6\r\n", 6),
                ("mixed-case", "content-length: 6\r\n", 6),
                ("empty", "Content-Length: 0\r\n", 0),
            })
                        yield return new TestCaseData(secure, fragment, headers, length)
                            .SetName($"FixedLengthBodiesAndSequentialRequestsPreserveBoundaries({secure},{fragment},{name})");
        }

        [TestCase(false, false, 8191)]
        [TestCase(false, false, 8192)]
        [TestCase(false, false, 16385)]
        [TestCase(true, false, 8191)]
        [TestCase(true, false, 8192)]
        [TestCase(true, false, 16385)]
        [TestCase(false, true, 8191)]
        [TestCase(false, true, 8192)]
        [TestCase(false, true, 16385)]
        [TestCase(true, true, 8191)]
        [TestCase(true, true, 8192)]
        [TestCase(true, true, 16385)]
        public async Task PipelineAfterPartiallyReadBodySurvivesTransportBufferReuse(bool secure, bool chunked, int length)
        {
            using var fixture = new RawListener(secure);
            await fixture.Connect();
            var payload = new string('b', length);
            var framing = chunked ? "Transfer-Encoding: chunked" : $"Content-Length: {length}";
            var body = chunked ? $"{length:x}\r\n{payload}\r\n0\r\nX-Trailer: end\r\n\r\n" : payload;
            await fixture.Write($"POST /body HTTP/1.1\r\nHost: 127.0.0.1\r\n{framing}\r\n\r\n{body}GET /second HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Padding: {new string('p', 8200)}\r\n\r\nGET /third HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", 8192);
            var first = await fixture.Listener.GetContextAsync(fixture.Token);
            Assert.That(first.Request.RawTarget, Is.EqualTo("/body"));
            var prefix = new byte[13];
            await first.Request.InputStream.ReadExactlyAsync(prefix, fixture.Token);
            Assert.That(prefix, Is.All.EqualTo((byte)'b'));
            Respond(first);
            Assert.That(await fixture.ReadHeaders(), Does.StartWith("HTTP/1.1 204 "));
            var second = await fixture.Listener.GetContextAsync(fixture.Token);
            Assert.That(second.Request.RawTarget, Is.EqualTo("/second"));
            Assert.That(second.Request.Headers["X-Padding"], Is.EqualTo(new string('p', 8200)));
            Respond(second);
            Assert.That(await fixture.ReadHeaders(), Does.StartWith("HTTP/1.1 204 "));
            var third = await fixture.Listener.GetContextAsync(fixture.Token);
            Assert.That(third.Request.RawTarget, Is.EqualTo("/third"));
            Respond(third, false);
            Assert.That(await fixture.ReadHeaders(), Does.StartWith("HTTP/1.1 204 "));
        }

        private static void Respond(IHttpContextImpl context, bool keepAlive = true)
        {
            context.Response.StatusCode = 204;
            context.Response.ContentLength64 = 0;
            context.Response.KeepAlive = keepAlive;
            context.Response.OutputStream.Write(Array.Empty<byte>(), 0, 0);
            context.Close();
        }

        private sealed class RawListener : IDisposable
        {
            private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
            internal readonly System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate;
            internal readonly Net.HttpListener Listener;
            internal readonly TcpClient Client = new();
            internal readonly string Url;
            internal Stream Stream = Stream.Null;
            internal CancellationToken Token => _timeout.Token;
            internal int Port => new Uri(Url).Port;

            internal RawListener(bool secure)
            {
                Certificate = secure ? HttpsSmoke.CreateCertificate() : null;
                Url = HttpsSmoke.GetUrl();
                if (!secure) Url = Url.Replace("https://", "http://", StringComparison.Ordinal);
                Listener = new Net.HttpListener(Certificate);
                Listener.AddPrefix(Url);
                Listener.Start();
            }

            internal async Task Connect()
            {
                Client.NoDelay = true;
                await Client.ConnectAsync("127.0.0.1", Port, Token);
                Stream = Client.GetStream();
                if (Certificate == null) return;
                var expected = Certificate.GetCertHashString(HashAlgorithmName.SHA256);
                var tls = new SslStream(Stream, false, (_, peer, _, errors) => peer != null
                    && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                    && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected);
                Stream = tls;
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, Token);
            }

            internal async Task Write(string text, int fragment)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                for (var offset = 0; offset < bytes.Length; offset += fragment)
                    await Stream.WriteAsync(bytes.AsMemory(offset, Math.Min(fragment, bytes.Length - offset)), Token);
            }

            internal async Task<string> ReadHeaders()
            {
                var header = new StringBuilder();
                var bytes = new byte[1];
                while (header.Length < 8192)
                {
                    var count = await Stream.ReadAsync(bytes.AsMemory(), Token);
                    if (count == 0) throw new EndOfStreamException();
                    header.Append((char)bytes[0]);
                    if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n")
                        return header.ToString();
                }
                throw new InvalidDataException("Oversized response header.");
            }

            internal async Task AssertHealthy()
            {
                using var client = Certificate != null ? HttpsSmoke.CreateClient(Certificate) : new HttpClient();
                var accept = Listener.GetContextAsync(Token);
                var pending = client.GetAsync(Url, Token);
                Respond(await accept, false);
                using var response = await pending;
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }

            public void Dispose()
            {
                _timeout.Cancel();
                Stream.Dispose();
                Client.Dispose();
                Listener.Dispose();
                Certificate?.Dispose();
                _timeout.Dispose();
            }
        }
    }
}
