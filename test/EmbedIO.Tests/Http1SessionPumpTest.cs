using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1SessionPumpTest
    {
        [Test]
        public async Task ResponseSignalRemainsSingleCompletionAcrossTokenWrap()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpConnection")
                ?? throw new AssertionException("Missing session coordinator.");
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var wait = type.GetMethod("WaitForResponseEndAsync", hidden) ?? throw new AssertionException("Missing response wait.");
            if (wait.ReturnType != typeof(ValueTask<bool>)) Assert.Ignore("Reusable signal is specific to the modern asset.");
            var owner = RuntimeHelpers.GetUninitializedObject(type);
            (type.GetField("_connectionSync", hidden) ?? throw new AssertionException("Missing session gate."))
                .SetValue(owner, new object());
            var reset = (type.GetMethod("ResetResponseCompletion", hidden) ?? throw new AssertionException("Missing response reset."))
                .CreateDelegate<Action>(owner);
            var pending = wait.CreateDelegate<Func<ValueTask<bool>>>(owner);
            var complete = (type.GetMethod("EndResponse", hidden) ?? throw new AssertionException("Missing response completion."))
                .CreateDelegate<Action<bool>>(owner);
            // More than the complete 16-bit token space. AsTask installs a real
            // continuation before each signal; duplicate stop signals must be safe.
            for (var cycle = 0; cycle < 65540; cycle++)
            {
                reset();
                var receiving = pending().AsTask();
                complete(false);
                complete(false);
                Assert.That(await receiving, Is.False);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task CloseWithoutOutputSendsFinalHeadAndKeepsNextRequestAligned(bool secure, bool head)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = new UriBuilder(HttpsSmoke.GetUrl())
            { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;
            using var listener = new Net.HttpListener(certificate);
            listener.AddPrefix(url.ToString());
            listener.Start();
            using var client = secure ? HttpsSmoke.CreateClient(certificate ?? throw new AssertionException("Missing certificate.")) : new HttpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var accepting = listener.GetContextAsync(timeout.Token);
            using var firstRequest = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, url);
            var firstResponse = client.SendAsync(firstRequest, timeout.Token);
            var first = await accepting;
            first.Response.StatusCode = head ? 200 : 204;
            first.Response.ContentLength64 = head ? 123 : 0;
            first.Close(); // No OutputStream access or zero-byte write.
            using var received = await firstResponse;
            Assert.That((int)received.StatusCode, Is.EqualTo(head ? 200 : 204));
            Assert.That(await received.Content.ReadAsByteArrayAsync(timeout.Token), Is.Empty);
            if (head) Assert.That(received.Content.Headers.ContentLength, Is.EqualTo(123));

            accepting = listener.GetContextAsync(timeout.Token);
            var secondResponse = client.GetAsync(url, timeout.Token);
            var second = await accepting;
            Assert.That(second.Request.RemoteEndPoint.Port, Is.EqualTo(first.Request.RemoteEndPoint.Port));
            Assert.Throws<ObjectDisposedException>(() => first.Response.OutputStream.Write(new byte[] { 1 }, 0, 1),
                "A completed response must never borrow the admitted successor's writer.");
            second.Response.StatusCode = 204;
            second.Response.ContentLength64 = 0;
            second.Close();
            using var next = await secondResponse;
            Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }
    }
}
