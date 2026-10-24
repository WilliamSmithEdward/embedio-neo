using System;
using System.Collections.Concurrent;
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
    // With address validation the server answers a new connection's first Initial with a
    // Retry (RFC 9000 8.1.2), and the .NET client completes its request through it (#520).
    // A relay between them records the first byte of every datagram the server sends: a
    // Retry is the only long-header type with all of 0xf0 set.
    public partial class Http3ListenerTest
    {
        [TestCase(true)]
        [TestCase(false)]
        public async Task AddressValidationRetriesBeforeTheHandshake(bool validate)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var port = new Uri(prefix).Port;
            using var server = new WebServer(options =>
                {
                    options
                        .WithUrlPrefix(prefix)
                        .WithMode(HttpListenerMode.EmbedIOHttp3)
                        .WithCertificate(certificate);

                    if (validate) options.WithHttp3AddressValidation();
                })
                .WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("served", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);

            try
            {
                using var relay = new UdpRelay(new IPEndPoint(IPAddress.Loopback, port));
                using var client = Client(certificate);

                for (var request = 0; request < 3; request++)
                {
                    // The relay listens on IPv4 only, and localhost may resolve to ::1 first.
                    using var message = Request(HttpMethod.Get, $"https://127.0.0.1:{relay.Port}/");
                    // The server routes by authority, which must name its own port.
                    message.Headers.Host = $"localhost:{port}";
                    using var response = await client.SendAsync(message, stop.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("served"));
                }

                Assert.That(relay.ServerFirstBytes.Any(first => (first & 0xf0) == 0xf0), Is.EqualTo(validate));
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Test]
        public void AddressValidationNeedsAnHttp3Mode()
        {
            var options = new WebServerOptions()
                .WithUrlPrefix("http://localhost:9696/")
                .WithMode(HttpListenerMode.EmbedIO)
                .WithHttp3AddressValidation();
            var refusal = Assert.Throws<NotSupportedException>(() => _ = new WebServer(options));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(refusal?.Message, Does.Contain("EmbedIOHttp3").And.Contain("EmbedIOCombined"));
                Assert.That(new WebServerOptions().RequireHttp3AddressValidation, Is.False);
                Assert.Throws<InvalidOperationException>(() => options.RequireHttp3AddressValidation = false);
            }
        }

        // Forwards one client's datagrams to the server and back.
        private sealed class UdpRelay : IDisposable
        {
            private readonly Socket _front = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly Socket _back = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly CancellationTokenSource _stop = new();
            private readonly Task[] _loops;
            private EndPoint? _client;

            internal UdpRelay(IPEndPoint server)
            {
                _front.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _back.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _back.Connect(server);
                _loops = [Task.Run(ForwardAsync), Task.Run(ReturnAsync)];
            }

            internal ConcurrentQueue<byte> ServerFirstBytes { get; } = new();

            internal int Port => ((IPEndPoint)(_front.LocalEndPoint ?? throw new InvalidOperationException("Unbound relay."))).Port;

            private async Task ForwardAsync()
            {
                var buffer = new byte[65535];

                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var received = await _front.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                        Volatile.Write(ref _client, received.RemoteEndPoint);
                        await _back.SendAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, _stop.Token);
                    }

                    // Windows reports ICMP port-unreachable for earlier datagrams on the next receive.
                    catch (SocketException) { }

                    catch (OperationCanceledException) { return; }
                }
            }

            private async Task ReturnAsync()
            {
                var buffer = new byte[65535];

                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var count = await _back.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                        ServerFirstBytes.Enqueue(buffer[0]);

                        if (Volatile.Read(ref _client) is { } client)
                            await _front.SendToAsync(buffer.AsMemory(0, count), SocketFlags.None, client, _stop.Token);
                    }

                    catch (SocketException) { }

                    catch (OperationCanceledException) { return; }
                }
            }

            public void Dispose()
            {
                _stop.Cancel();
                _front.Dispose();
                _back.Dispose();

                try
                {
                    Task.WaitAll(_loops, TimeSpan.FromSeconds(5));
                }

                catch (AggregateException) { }

                _stop.Dispose();
            }
        }
    }
}
