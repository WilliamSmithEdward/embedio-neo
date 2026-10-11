using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1IdleClosureTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(false)]
        [TestCase(true)]
        public async Task Tls12RecordObservationDistinguishesTimeoutFromSendShutdown(bool orderlyControl)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1" }.Uri;
            using var listener = new Net.HttpListener(certificate);
            listener.AddPrefix(url.ToString());
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, url.Port, deadline.Token);
            using var wire = peer.GetStream();
            using var tls = new SslStream(wire, true, (_, remote, _, _) =>
                remote != null && remote.GetCertHashString() == certificate.GetCertHashString());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12,
                ApplicationProtocols = new() { SslApplicationProtocol.Http11 },
            }, deadline.Token);
            var accepting = listener.GetContextAsync(deadline.Token);
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {url.Authority}\r\n\r\n"), deadline.Token);
            var context = await accepting;
            var connection = context.GetType().GetProperty("Connection", Hidden)?.GetValue(context)
                ?? throw new AssertionException("Missing accepted connection.");
            var type = connection.GetType();
            var sync = type.GetField("_connectionSync", Hidden)?.GetValue(connection)
                ?? throw new AssertionException("Missing connection gate.");
            context.Response.StatusCode = 204;
            context.Response.ContentLength64 = 0;
            context.Close();
            await ReadHeadAsync(tls, deadline.Token);
            while (true)
            {
                lock (sync)
                    if ((int)(type.GetProperty("Reuses")?.GetValue(connection) ?? 0) == 1) break;
                await Task.Delay(1, deadline.Token);
            }
            if (orderlyControl)
            {
                // Test-only comparison using the existing public-API tunnel
                // send-shutdown primitive. Production idle closure is untouched.
                var shutdown = type.GetMethod("CompleteTunnelOutputAsync", Hidden)?.CreateDelegate<Func<CancellationToken, Task>>(connection)
                    ?? throw new AssertionException("Missing TLS send shutdown.");
                await shutdown(deadline.Token);
                (type.GetMethod("ForceClose", Hidden)?.CreateDelegate<Action>(connection)
                    ?? throw new AssertionException("Missing terminal cleanup."))();
            }
            else
            {
                var timer = type.GetField("_timer", Hidden)?.GetValue(connection) as Timer
                    ?? throw new AssertionException("Missing timer.");
                lock (sync) timer.Change(1, Timeout.Infinite);
            }
            // No SslStream read is pending on this peer. TLS 1.2 exposes the
            // encrypted record's content type; TLS 1.3 hides alerts as type 23.
            // Reading the raw wire here does not establish the alert description.
            using var records = new MemoryStream();
            try { await wire.CopyToAsync(records, deadline.Token); }
            catch (IOException error) { TestContext.Out.WriteLine(error); }
            var bytes = records.ToArray();
            TestContext.Out.WriteLine($"TLS 1.2 {(orderlyControl ? "send-shutdown control" : "timeout")}: raw trailing bytes={bytes.Length}; hex={Convert.ToHexString(bytes)}");
            if (orderlyControl)
            {
                Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(5));
                Assert.That(bytes[0], Is.EqualTo(21), "The public TLS shutdown control must produce an alert record.");
                Assert.That(bytes.Length, Is.EqualTo(5 + (bytes[3] << 8) + bytes[4]), "Expected one complete TLS alert record.");
            }
        }

        [TestCase(false, "idle")]
        [TestCase(true, "idle")]
        [TestCase(false, "partial-head")]
        [TestCase(true, "partial-head")]
        [TestCase(false, "unread-body-abort")]
        [TestCase(true, "unread-body-abort")]
        [TestCase(false, "partial-response-abort")]
        [TestCase(true, "partial-response-abort")]
        [TestCase(false, "peer-reset")]
        [TestCase(true, "peer-reset")]
        [TestCase(false, "reuse-race")]
        [TestCase(true, "reuse-race")]
        public async Task ClosureObservationReleasesResourcesAndPreservesListener(bool secure, string scenario)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = new UriBuilder(HttpsSmoke.GetUrl())
            { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;
            using var listener = new Net.HttpListener(certificate);
            listener.AddPrefix(url.ToString());
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, url.Port, deadline.Token);
            using var wire = peer.GetStream();
            using var tls = secure ? new SslStream(wire, true, (_, remote, _, _) =>
                remote != null && remote.GetCertHashString() == certificate?.GetCertHashString()) : null;
            if (tls != null)
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = "localhost", ApplicationProtocols = new() { SslApplicationProtocol.Http11 } }, deadline.Token);
            Stream transport = tls ?? (Stream)wire;
            var accepting = listener.GetContextAsync(deadline.Token);
            var request = $"GET / HTTP/1.1\r\nHost: {url.Authority}\r\n\r\n";
            if (scenario == "unread-body-abort")
                request = $"POST / HTTP/1.1\r\nHost: {url.Authority}\r\nContent-Length: 100\r\n\r\nx";
            await transport.WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
            var context = await accepting;
            var connection = context.GetType().GetProperty("Connection", Hidden)?.GetValue(context)
                ?? throw new AssertionException("Missing accepted connection.");
            var type = connection.GetType();
            var sync = type.GetField("_connectionSync", Hidden)?.GetValue(connection)
                ?? throw new AssertionException("Missing connection gate.");
            var timer = type.GetField("_timer", Hidden)?.GetValue(connection) as Timer
                ?? throw new AssertionException("Missing request timer.");
            var force = type.GetMethod("ForceClose", Hidden)?.CreateDelegate<Action>(connection)
                ?? throw new AssertionException("Missing abort operation.");
            var finished = type.GetField("_closeFinished", Hidden)
                ?? throw new AssertionException("Missing cleanup completion.");
            bool Closed() { lock (sync) return (bool)(finished.GetValue(connection) ?? false); }

            if (scenario == "unread-body-abort") force();
            else if (scenario == "partial-response-abort")
            {
                context.Response.ContentLength64 = 100;
                await context.Response.OutputStream.WriteAsync(new byte[] { 42 }, deadline.Token);
                await context.Response.OutputStream.FlushAsync(deadline.Token);
                await ReadHeadAsync(transport, deadline.Token);
                var body = new byte[1];
                Assert.That(await transport.ReadAsync(body, deadline.Token), Is.EqualTo(1));
                Assert.That(body[0], Is.EqualTo(42));
                force();
            }
            else
            {
                context.Response.StatusCode = 204;
                context.Response.ContentLength64 = 0;
                context.Close();
                var head = await ReadHeadAsync(transport, deadline.Token);
                Assert.That(head, Does.StartWith("HTTP/1.1 204 "));
                Assert.That(head, Does.Contain("timeout=15"));
                // Observe the actual actor transition before touching its timer.
                while (true)
                {
                    lock (sync)
                        if ((int)(type.GetProperty("Reuses")?.GetValue(connection) ?? 0) == 1) break;
                    await Task.Delay(1, deadline.Token);
                }
                if (scenario == "partial-head")
                    await transport.WriteAsync(Encoding.ASCII.GetBytes("GET /partial HTTP/1.1\r\nHost:"), deadline.Token);
                if (scenario == "peer-reset")
                {
                    peer.Client.LingerState = new LingerOption(true, 0);
                    peer.Dispose();
                }
                else if (scenario != "idle")
                {
                    // Preserve the real 15-second default in the idle cases;
                    // accelerate only the partial-head and reuse race controls.
                    lock (sync) timer.Change(1, Timeout.Infinite);
                    if (scenario == "reuse-race")
                    {
                        accepting = listener.GetContextAsync(deadline.Token);
                        try { await transport.WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token); }
                        catch (IOException error) { TestContext.Out.WriteLine("race-write: " + error); }
                    }
                }
            }

            // A racing complete request may win admission. Complete it so that
            // stopping the header timer cannot turn this into a hanging fixture.
            if (scenario == "reuse-race")
            {
                while (!Closed() && !accepting.IsCompleted) await Task.Delay(1, deadline.Token);
                if (accepting.IsCompletedSuccessfully)
                {
                    var next = await accepting;
                    next.Response.StatusCode = 204;
                    next.Response.ContentLength64 = 0;
                    next.Response.KeepAlive = false;
                    next.Close();
                }
            }
            if (scenario != "peer-reset")
            {
                using var observed = new MemoryStream();
                var bytes = new byte[1024];
                try
                {
                    int count;
                    while ((count = await transport.ReadAsync(bytes, deadline.Token)) != 0)
                        observed.Write(bytes, 0, count);
                    TestContext.Out.WriteLine($"{scenario}/{(secure ? "TLS" : "TCP")}: peer EOF; trailing bytes={observed.Length}");
                }
                catch (IOException error)
                {
                    TestContext.Out.WriteLine($"{scenario}/{(secure ? "TLS" : "TCP")}: trailing bytes={observed.Length}; {error}");
                }
                if (scenario != "reuse-race") Assert.That(observed.Length, Is.Zero, "Closure must not synthesize a response or finish an aborted body.");
                else if (observed.Length != 0)
                    Assert.That(Encoding.ASCII.GetString(observed.ToArray()), Does.StartWith("HTTP/1.1 204 "));
            }
            while (!Closed()) await Task.Delay(1, deadline.Token);
            Assert.That(type.GetField("_buffer", Hidden)?.GetValue(connection), Is.Null);
            Assert.That((type.GetProperty("Stream")?.GetValue(connection) as Stream)?.CanRead, Is.False);
            var timerDisposed = false;
            try { timerDisposed = !timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { timerDisposed = true; }
            Assert.That(timerDisposed, Is.True);
            Assert.That(listener.IsListening, Is.True);
            deadline.Cancel();
            if (scenario == "reuse-race" && !accepting.IsCompletedSuccessfully)
            {
                try { await accepting; }
                catch (OperationCanceledException) { }
            }

            // A fresh peer on the same listener must remain healthy.
            using var healthy = secure ? HttpsSmoke.CreateClient(certificate ?? throw new AssertionException("Missing certificate."))
                : new System.Net.Http.HttpClient();
            using var healthDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var healthAccept = listener.GetContextAsync(healthDeadline.Token);
            var healthReply = healthy.GetAsync(url, healthDeadline.Token);
            var healthContext = await healthAccept;
            healthContext.Response.StatusCode = 204;
            healthContext.Response.ContentLength64 = 0;
            healthContext.Close();
            using var healthResponse = await healthReply;
            Assert.That(healthResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken token)
        {
            using var bytes = new MemoryStream();
            var next = new byte[1];
            while (bytes.Length < 8192)
            {
                Assert.That(await stream.ReadAsync(next, token), Is.EqualTo(1), "Response ended before its head.");
                bytes.WriteByte(next[0]);
                var head = Encoding.ASCII.GetString(bytes.ToArray());
                if (head.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return head;
            }
            throw new AssertionException("Response head exceeds fixture bound.");
        }
    }
}
