using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Health;
using EmbedIO.Idempotency;
using NUnit.Framework;
using Coordinator = EmbedIO.Idempotency.Idempotency;

namespace EmbedIO.Tests
{
    public sealed partial class IdempotencyTest
    {
        [Test]
        public async Task DisconnectedOwnerKeepsCompletedResultForRetry()
        {
            using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            var prefix = "http://127.0.0.1:" + port + "/"; var store = new DeliveryStore(); var coordinator = new Coordinator(store); var calls = 0;
            var entered = Signal(); var release = Signal(); var policy = new IdempotencyPolicy("delivery-fixture", _ => "local-test", TimeSpan.FromMinutes(1));
            using var server = new WebServer(prefix).WithAction("/", HttpVerbs.Post, c => coordinator.ExecuteAsync(c, policy, async staged =>
            { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; await Write(staged, "done"); }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var ready = new HealthCheckService(server); var running = server.RunAsync(stop.Token);
            try
            {
                await ready.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                using (var abandoned = new TcpClient())
                {
                    await abandoned.ConnectAsync(IPAddress.Loopback, port, stop.Token);
                    var bytes = Encoding.ASCII.GetBytes("POST /write HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Length: 7\r\nContent-Type: application/json; charset=utf-8\r\nIdempotency-Key: one\r\n\r\npayload");
                    await abandoned.GetStream().WriteAsync(bytes, stop.Token);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); abandoned.Client.LingerState = new LingerOption(true, 0);
                }
                release.TrySetResult(); await store.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var handler = new SocketsHttpHandler { UseProxy = false }; using var client = new HttpClient(handler);
                using var request = new HttpRequestMessage(HttpMethod.Post, prefix + "write") { Content = new StringContent("payload", Encoding.UTF8, "application/json") }; request.Headers.Add("Idempotency-Key", "one");
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("done")); Assert.That(calls, Is.EqualTo(1));
            }
            finally { release.TrySetResult(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        private sealed class DeliveryStore : IIdempotencyStore
        {
            private readonly MemoryIdempotencyStore _inner = new MemoryIdempotencyStore();
            internal readonly TaskCompletionSource Committed = Signal();
            public Task<IdempotencyRecord> AcquireAsync(string key, string fingerprint, TimeSpan retention, CancellationToken token) => _inner.AcquireAsync(key, fingerprint, retention, token);
            public async Task<bool> CompleteAsync(string key, string owner, IdempotencyResponse response, CancellationToken token)
            { var result = await _inner.CompleteAsync(key, owner, response, token); if (result) Committed.TrySetResult(); return result; }
            public Task<bool> FailAsync(string key, string owner, CancellationToken token) => _inner.FailAsync(key, owner, token);
        }
        [TestCase(10)]
        [TestCase(11)]
        [TestCase(20)]
        public async Task IdempotencyReplaysFiniteWritesOnOwnedTcpTransports(int protocol)
        {
            using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            var prefix = "http://127.0.0.1:" + port + "/"; var coordinator = new Coordinator(); var calls = 0;
            using var server = new WebServer(prefix).WithAction("/", HttpVerbs.Post, c => coordinator.ExecuteAsync(c, new IdempotencyPolicy("tcp-fixture", _ => "local-test", TimeSpan.FromMinutes(1)), staged => Write(staged, (++calls).ToString())));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var ready = new HealthCheckService(server); var running = server.RunAsync(stop.Token);
            try
            {
                await ready.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                using var handler = new SocketsHttpHandler { UseProxy = false }; using var client = new HttpClient(handler);
                var version = protocol == 10 ? HttpVersion.Version10 : protocol == 20 ? HttpVersion.Version20 : HttpVersion.Version11;
                for (var i = 0; i < 3; i++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, prefix + "write") { Content = new StringContent(i == 2 ? "different" : "same"), Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                    request.Headers.Add("Idempotency-Key", "one"); using var response = await client.SendAsync(request, stop.Token);
                    Assert.That(response.Version, Is.EqualTo(version)); Assert.That(response.StatusCode, Is.EqualTo(i == 2 ? HttpStatusCode.Conflict : HttpStatusCode.OK));
                    if (i < 2) Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("1"));
                }
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
    public partial class Http3ListenerTest
    {
        [Test]
        public async Task IdempotencyReplaysCompressedFiniteWritesOnHttp3()
        {
            using var certificate = Certificate(); var prefix = Prefix(); var coordinator = new Coordinator(); var calls = 0;
            var policy = new IdempotencyPolicy("http3-fixture", _ => "local-test", TimeSpan.FromMinutes(1)); var body = new string('x', 2048);
            using var server = new WebServer(options => options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Post, c => coordinator.ExecuteAsync(c, policy, staged =>
                { Interlocked.Increment(ref calls); return ResponseSerializer.None(false)(staged, body); }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var running = server.RunAsync(stop.Token);
            using var client = Client(certificate); client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip");
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    using var request = Request(HttpMethod.Post, prefix + "write", new StringContent("same")); request.Headers.Add("Idempotency-Key", "one");
                    using var response = await client.SendAsync(request, stop.Token); Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30)); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var gzip = new System.IO.Compression.GZipStream(await response.Content.ReadAsStreamAsync(stop.Token), System.IO.Compression.CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip); Assert.That(await reader.ReadToEndAsync(stop.Token), Is.EqualTo(body));
                }
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { stop.Cancel(); await running; }
        }
    }
}
