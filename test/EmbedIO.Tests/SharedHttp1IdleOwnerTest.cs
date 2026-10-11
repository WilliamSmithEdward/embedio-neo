using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class SharedHttp1IdleOwnerTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(false, false, false, "idle")]
        [TestCase(false, true, false, "idle")]
        [TestCase(true, false, false, "idle")]
        [TestCase(true, true, false, "idle")]
        [TestCase(false, false, true, "idle")]
        [TestCase(false, true, true, "idle")]
        [TestCase(true, false, true, "idle")]
        [TestCase(true, true, true, "idle")]
        [TestCase(false, false, false, "boundary")]
        [TestCase(false, true, false, "boundary")]
        [TestCase(true, false, false, "boundary")]
        [TestCase(true, true, false, "boundary")]
        [TestCase(false, false, false, "unread")]
        [TestCase(false, true, false, "unread")]
        [TestCase(true, false, false, "unread")]
        [TestCase(true, true, false, "unread")]
        [TestCase(false, false, false, "active")]
        [TestCase(false, true, false, "active")]
        [TestCase(true, false, false, "active")]
        [TestCase(true, true, false, "active")]
        [TestCase(false, false, false, "drain")]
        [TestCase(false, false, true, "drain")]
        [TestCase(true, false, false, "drain")]
        [TestCase(true, false, true, "drain")]
        public async Task RetiringAnIdleOwnerPreservesSharedTransportButFinalOwnerClosesIt(bool secure, bool dispose, bool finalOwner, string phase)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var prefix = new UriBuilder(Resources.GetServerAddress())
            { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var front = new Net.HttpListener(certificate);
            using var api = new Net.HttpListener(certificate);
            front.AddPrefix(prefix + "front/");
            api.AddPrefix(prefix + "api/");
            front.Start();
            api.Start();
            using var peer = new TcpClient();
            await peer.ConnectAsync(prefix.Host, prefix.Port, timeout.Token);
            using Stream wire = secure
                ? new SslStream(peer.GetStream(), false, (_, presented, _, _) =>
                    presented != null && certificate != null
                    && Convert.ToHexString(presented.GetRawCertData()) == Convert.ToHexString(certificate.RawData))
                : peer.GetStream();
            if (wire is SslStream tls)
                await tls.AuthenticateAsClientAsync("localhost");

            var accepting = api.GetContextAsync(timeout.Token);
            if (phase == "unread")
                await wire.WriteAsync(Encoding.ASCII.GetBytes(
                    $"POST /api/value HTTP/1.1\r\nHost: {prefix.Host}:{prefix.Port}\r\nContent-Length: 10\r\n\r\n"), timeout.Token);
            else await Request(wire, prefix, "/api/value", timeout.Token);
            var first = await accepting;
            var session = first.GetType().GetProperty("Connection", Hidden)?.GetValue(first)
                ?? throw new AssertionException("Missing accepted TCP session.");
            void Retire()
            {
                if (finalOwner) front.Dispose();
                if (phase == "drain")
                {
                    var graceful = api.GetType().GetInterface("EmbedIO.Net.Internal.IGracefulHttpListener")
                        ?? throw new AssertionException("Missing graceful listener contract.");
                    var drain = graceful.GetMethod("DrainAsync")
                        ?? throw new AssertionException("Missing graceful drain.");
                    var pending = drain.Invoke(api, new object[] { TimeSpan.FromSeconds(5), timeout.Token }) as Task
                        ?? throw new AssertionException("Missing drain completion.");
                    pending.GetAwaiter().GetResult();
                }
                else if (dispose) api.Dispose();
                else api.Stop();
            }

            first.Response.StatusCode = phase == "active" ? 200 : 204;
            first.Response.ContentLength64 = phase == "active" ? 10 : 0;
            if (phase == "active")
            {
                await first.Response.OutputStream.WriteAsync(new byte[] { 65 }, timeout.Token);
                Retire();
            }
            else if (phase == "boundary")
            {
                var gate = session.GetType().GetField("_connectionSync", Hidden)?.GetValue(session)
                    ?? throw new AssertionException("Missing session gate.");
                lock (gate)
                {
                    first.Close();
                    Retire();
                }
                await Head(wire, timeout.Token);
            }
            else
            {
                first.Close();
                await Head(wire, timeout.Token);
                if (phase is "idle" or "drain") await Idle(session, timeout.Token);
                Retire();
            }

            if (phase is "active" or "unread")
            {
                Assert.That(TcpSessionProbe.Closed(session), Is.True,
                    "Retirement must abort unfinished responses and unread request bodies.");
                return;
            }

            if (finalOwner)
            {
                Assert.That(TcpSessionProbe.Closed(session), Is.True, "Final endpoint retirement must release an idle session.");
                return;
            }

            Assert.That(TcpSessionProbe.Closed(session), Is.False,
                "An idle transport can still route to the surviving listener; its last request is not its current owner.");
            accepting = front.GetContextAsync(timeout.Token);
            await Request(wire, prefix, "/front/value", timeout.Token);
            var next = await accepting;
            Assert.That(next.Request.RemoteEndPoint.Port, Is.EqualTo(first.Request.RemoteEndPoint.Port),
                "A retry on a fresh connection must not hide the ownership failure.");
            next.Response.StatusCode = 204;
            next.Response.ContentLength64 = 0;
            next.Close();
            await Head(wire, timeout.Token);
            front.Dispose();
            Assert.That(TcpSessionProbe.Closed(session), Is.True, "The surviving final owner must still release the session.");
        }

        private static async Task Idle(object session, CancellationToken token)
        {
            var type = session.GetType();
            var gate = type.GetField("_connectionSync", Hidden)?.GetValue(session)
                ?? throw new AssertionException("Missing session gate.");
            var bound = type.GetField("_contextBound", Hidden)
                ?? throw new AssertionException("Missing request ownership flag.");
            var prefix = type.GetProperty("Prefix", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new AssertionException("Missing parsed prefix.");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                    if (bound.GetValue(session) is false && prefix.GetValue(session) == null) return;
                await Task.Yield();
            }
        }

        private static Task Request(Stream stream, Uri prefix, string path, CancellationToken token)
            => stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {prefix.Host}:{prefix.Port}\r\n\r\n"), token).AsTask();

        private static async Task Head(Stream stream, CancellationToken token)
        {
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.That(header.Length, Is.LessThan(4096));
                var read = await stream.ReadAsync(one, token);
                if (read == 0) throw new IOException("The shared transport closed before its response head.");
                header.Append((char)one[0]);
            }
            Assert.That(header.ToString(), Does.StartWith("HTTP/1.1 204 "));
        }
    }
}
