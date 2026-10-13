using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1OrderlyIdleClosureTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task AdmissionAndIdleExpiryHaveDeterministicWinners(bool secure, bool admissionWins)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var uri = new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;
            using var listener = new Net.HttpListener(certificate);
            listener.AddPrefix(uri.ToString());
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, uri.Port, deadline.Token);
            using var wire = peer.GetStream();
            using var tls = secure ? new SslStream(wire, true, (_, remote, _, _) =>
                remote != null && remote.GetCertHashString() == certificate?.GetCertHashString()) : null;
            if (tls != null)
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = "localhost", ApplicationProtocols = new() { SslApplicationProtocol.Http11 } }, deadline.Token);
            Stream stream = tls ?? (Stream)wire;
            var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {uri.Authority}\r\n\r\n");
            var accepting = listener.GetContextAsync(deadline.Token);
            await stream.WriteAsync(request, deadline.Token);
            var first = await accepting;
            var connection = first.GetType().GetProperty("Connection", Hidden)?.GetValue(first)
                ?? throw new AssertionException("Missing connection.");
            var type = connection.GetType();
            var gate = type.GetField("_connectionSync", Hidden)?.GetValue(connection)
                ?? throw new AssertionException("Missing admission gate.");
            var expire = type.GetMethod("CloseTransport", Hidden)?.CreateDelegate<Action<bool, bool>>(connection)
                ?? throw new AssertionException("Missing retirement operation.");
            Reply(first);
            await ReadHeadAsync(stream, deadline.Token);
            await WaitForIdleAsync(connection, gate, deadline.Token);

            if (admissionWins)
            {
                accepting = listener.GetContextAsync(deadline.Token);
                await stream.WriteAsync(request, deadline.Token);
                var successor = await accepting;
                // A timer queued for the preceding idle state must not retire
                // the admitted request, whose deadline has already been stopped.
                expire(false, true);
                Assert.That(TestObjects.TcpSessionProbe.Closed(connection), Is.False);
                Reply(successor);
                await ReadHeadAsync(stream, deadline.Token);
                await WaitForIdleAsync(connection, gate, deadline.Token);
            }
            lock (gate)
            {
                var lifetime = TestObjects.TcpSessionProbe.Lifetime(connection);
                TestObjects.TcpSessionProbe.Timer(connection).Change(Timeout.Infinite, Timeout.Infinite);
                lifetime.GetType().GetField("_deadline", Hidden)?.SetValue(lifetime, Stopwatch.GetTimestamp() - 1);
                expire(false, true);
            }
            Assert.That(await stream.ReadAsync(new byte[1], deadline.Token), Is.Zero, "A completed idle exchange must end with EOF.");
            await TestObjects.TcpSessionProbe.Completion(connection).WaitAsync(deadline.Token);
            Assert.That(TestObjects.TcpSessionProbe.Closed(connection), Is.True);
            Assert.That(listener.IsListening, Is.True);

            try { await stream.WriteAsync(request, deadline.Token); }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            { TestContext.Out.WriteLine("Retired peer rejected successor write: " + error.GetType().Name); }

            // Once expiry wins, no already-retired connection may admit a
            // successor, and the same listener must still accept a fresh peer.
            using var fresh = secure ? HttpsSmoke.CreateClient(certificate ?? throw new AssertionException("Missing certificate."))
                : new System.Net.Http.HttpClient();
            accepting = listener.GetContextAsync(deadline.Token);
            var fetching = fresh.GetAsync(uri, deadline.Token);
            var healthy = await accepting;
            Assert.That(healthy.Request.RemoteEndPoint.Port, Is.Not.EqualTo(first.Request.RemoteEndPoint.Port));
            Reply(healthy);
            using var response = await fetching;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        private static void Reply(IHttpContextImpl context)
        {
            context.Response.StatusCode = 204;
            context.Response.ContentLength64 = 0;
            context.Close();
        }

        private static async Task WaitForIdleAsync(object connection, object gate, CancellationToken token)
        {
            while (true)
            {
                lock (gate)
                {
                    var type = connection.GetType();
                    if (type.GetField("_idleAwaitingSuccessor", Hidden)?.GetValue(connection) is true
                        && type.GetField("_idleRead", Hidden)?.GetValue(connection) is Task { IsCompleted: false })
                        return;
                }
                await Task.Delay(1, token);
            }
        }

        private static async Task ReadHeadAsync(Stream stream, CancellationToken token)
        {
            using var bytes = new MemoryStream();
            var next = new byte[1];
            while (bytes.Length < 8192)
            {
                Assert.That(await stream.ReadAsync(next, token), Is.EqualTo(1));
                bytes.WriteByte(next[0]);
                if (Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal)) return;
            }
            throw new AssertionException("Response head exceeds fixture bound.");
        }
    }
}
