using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase(1)]
        [TestCase(96)]
        public async Task CombinedListenerDispatchesEveryProtocol(int perProtocol)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var count = 0;
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix)
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    Interlocked.Increment(ref count);
                    await context.SendStringAsync(context.Request.ProtocolVersion.ToString(), "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            try
            {
                await Task.WhenAll(new[] { HttpVersion.Version11, HttpVersion.Version20, HttpVersion.Version30 }.Select(async version =>
                {
                    using var client = Client(certificate);
                    client.DefaultRequestVersion = version;
                    await Task.WhenAll(Enumerable.Range(0, perProtocol).Select(async _ =>
                    {
                        using var response = await client.GetAsync(prefix, stop.Token);
                        Assert.That(response.Version, Is.EqualTo(version));
                        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(version.ToString()));
                    }));
                }));
                Assert.That(count, Is.EqualTo(3 * perProtocol));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task CombinedListenerCanceledConsumerDoesNotLoseNextRequest(bool quic)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            var listener = server.Listener;
            listener.Start();
            using var canceled = new CancellationTokenSource();
            var abandoned = listener.GetContextAsync(canceled.Token);
            canceled.Cancel();
            await Assert.ThatAsync(async () => await abandoned, Throws.InstanceOf<OperationCanceledException>());
            using var client = Client(certificate);
            client.DefaultRequestVersion = quic ? HttpVersion.Version30 : HttpVersion.Version20;
            var request = client.GetAsync(prefix);
            var context = await listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(context.Request.ProtocolVersion, Is.EqualTo(client.DefaultRequestVersion));
            context.Response.ContentLength64 = 0;
            context.Close();
            using var response = await request;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task CombinedListenerStopCompletesPendingAcceptAndRestarts(bool dispose)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            var listener = server.Listener;
            listener.Start();
            var pending = listener.GetContextAsync(CancellationToken.None);
            if (dispose) listener.Dispose(); else listener.Stop();
            await Assert.ThatAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<HttpListenerException>());
            Assert.That(listener.IsListening, Is.False);
            if (dispose)
            {
                Assert.That(() => listener.Start(), Throws.InstanceOf<ObjectDisposedException>());
                return;
            }
            listener.Start();
            using var client = Client(certificate);
            var request = client.GetAsync(prefix);
            var context = await listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            context.Response.ContentLength64 = 0;
            context.Close();
            using var response = await request;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task CombinedListenerStopAbortsUndispatchedResponses(int version)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            server.Listener.Start();
            using var client = Client(certificate);
            client.DefaultRequestVersion = version == 1 ? HttpVersion.Version11 : new Version(version, 0);
            var requests = Enumerable.Range(0, 32).Select(_ => client.GetAsync(prefix)).ToArray();
            // One dequeued request proves that dispatch is live; none is completed.
            _ = await server.Listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            server.Listener.Stop();
            foreach (var request in requests)
                await Assert.ThatAsync(async () => { using var response = await request.WaitAsync(TimeSpan.FromSeconds(5)); },
                    Throws.InstanceOf<HttpRequestException>());
            Assert.That(server.Listener.IsListening, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task CombinedListenerFailedBindRollsBackAndCanRestart(bool udp)
        {
            using var certificate = Certificate();
            using var occupied = new Socket(AddressFamily.InterNetwork, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp);
            occupied.ExclusiveAddressUse = true;
            occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            if (!udp) occupied.Listen(1);
            var port = ((IPEndPoint)(occupied.LocalEndPoint ?? throw new AssertionException("Missing endpoint."))).Port;
            var prefix = $"https://localhost:{port}/";
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            Assert.That(() => server.Listener.Start(), Throws.Exception);
            Assert.That(server.Listener.IsListening, Is.False);
            // When UDP startup fails after TCP bound, TCP must already be released.
            if (udp)
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                probe.ExclusiveAddressUse = true;
                probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
            }
            occupied.Dispose();
            server.Listener.Start();
            Assert.That(server.Listener.IsListening, Is.True);
            using var client = Client(certificate);
            var request = client.GetAsync(prefix);
            var context = await server.Listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            context.Response.ContentLength64 = 0;
            context.Close();
            using var response = await request;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
