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

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExpectContinuePrecedesBodyAndAllowsNextRequest(bool secure)
        {
            using var fixture = new RawListener(secure);
            await fixture.Connect();
            var accept = fixture.Listener.GetContextAsync(fixture.Token);
            await fixture.Write("POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 6\r\nExpect: 100-continue\r\n\r\n", 7);
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
            try { await context.Request.InputStream.CopyToAsync(body, fixture.Token); }
            catch (IOException) when (secure) { }
            Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("ab"));
            // An application can observe EOF before Content-Length and close the response.
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
            var bytes = new byte[1];
            Assert.That(await fixture.Stream.ReadAsync(bytes.AsMemory(), fixture.Token), Is.Zero);
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
            foreach (var name in new[] { "_prefixes", "_unregistered" })
            {
                var field = endpointType.GetField(name, fields);
                (field ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(endpoint, Activator.CreateInstance(field.FieldType));
            }
            var prefixType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.ListenerPrefix", true);
            ((endpointType).GetMethod("AddPrefix") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(endpoint,
                new[] { Activator.CreateInstance((prefixType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), $"http://127.0.0.1:{port}/"), fixture.Listener });
            var clients = new List<TcpClient>();
            using var args = new SocketAsyncEventArgs { UserToken = endpoint };
            var completion = endpointType.GetMethod("ProcessAccept", BindingFlags.Static | BindingFlags.NonPublic);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            args.Completed += (_, completed) =>
            {
                var terminal = completed.SocketError != SocketError.Success;
                (completion ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { completed });
                if (terminal) closed.TrySetResult();
            };
            var armed = false;
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
                ((endpointType).GetMethod("Accept", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
                    .Invoke(null, new object?[] { socket, args, null });
                armed = true;
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
                if (armed) await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
