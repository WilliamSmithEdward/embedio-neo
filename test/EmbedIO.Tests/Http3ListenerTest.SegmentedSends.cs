using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        private const int SegmentedBodyLength = 1 << 20;

        // A 1 MiB response is about 900 datagrams. Consecutive ones for one peer leave
        // together where the platform segments UDP sends.
        [Test]
        public async Task LargeResponsesLeaveInSegmentedSends()
        {
            SegmentedUdpSendTest.RequireSegmentation();
            await using var fixture = await SegmentedFixture.StartAsync();
            var bindings = fixture.Bindings();

            foreach (var binding in bindings)
                Assert.That(SegmentField().GetValue(binding), Is.Not.Null, "The platform should segment UDP sends.");

            await fixture.DownloadTwiceAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Counter(bindings, "SegmentedSends"), Is.GreaterThan(0));
                Assert.That(
                    Counter(bindings, "SegmentedDatagrams"),
                    Is.GreaterThanOrEqualTo(2 * Counter(bindings, "SegmentedSends"))
                );
                Assert.That(Counter(bindings, "SegmentFallbacks"), Is.Zero);
            }
        }

        // Where the platform cannot segment, every datagram leaves alone, as before.
        [Test]
        public async Task ResponsesLeaveWhenTheSocketCannotSegment()
        {
            await using var fixture = await SegmentedFixture.StartAsync();
            var bindings = fixture.Bindings();

            foreach (var binding in bindings) SegmentField().SetValue(binding, null);

            var segmented = Counter(bindings, "SegmentedSends");
            await fixture.DownloadTwiceAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Counter(bindings, "SegmentedSends"), Is.EqualTo(segmented));
                Assert.That(Counter(bindings, "SegmentFallbacks"), Is.Zero);
            }
        }

        // A segmented send that would block, that the platform refuses this once, or that
        // meets a datagram path error leaves its datagrams one at a time and loses none; a
        // path error counts once, as for a single datagram.
        [Test]
        public async Task FailedSegmentedSendsLeaveOneDatagramAtATime()
        {
            SegmentedUdpSendTest.RequireSegmentation();
            await using var fixture = await SegmentedFixture.StartAsync();
            var bindings = fixture.Bindings();
            var failures = new[]
            {
                SocketError.WouldBlock,
                SocketError.NoBufferSpaceAvailable,
                SocketError.MessageSize,
                SocketError.InvalidArgument,
                SocketError.ConnectionReset
            };
            var calls = 0;

            foreach (var binding in bindings)
            {
                var real = (Func<SocketAddress, ReadOnlySpan<byte>, int, SocketError>?)SegmentField().GetValue(binding)
                    ?? throw new AssertionException("The platform should segment UDP sends.");
                SegmentField()
                    .SetValue(
                        binding,
                        new Func<SocketAddress, ReadOnlySpan<byte>, int, SocketError>((address, datagrams, length) =>
                        {
                            var call = Interlocked.Increment(ref calls);
                            return call <= failures.Length ? failures[call - 1] : real(address, datagrams, length);
                        })
                    );
            }

            var segmented = Counter(bindings, "SegmentedSends");
            var pathErrors = Counter(bindings, "DatagramPathErrors");
            await fixture.DownloadTwiceAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Counter(bindings, "SegmentFallbacks"), Is.EqualTo(failures.Length));
                Assert.That(Counter(bindings, "DatagramPathErrors"), Is.EqualTo(pathErrors + 1));
                Assert.That(Counter(bindings, "SegmentedSends"), Is.GreaterThan(segmented));
                Assert.That(bindings.All(binding => SegmentField().GetValue(binding) != null), Is.True);
            }
        }

        // A platform that reports it cannot segment at all is not asked again.
        [Test]
        public async Task UnsupportedSegmentationStopsSegmentedSends()
        {
            SegmentedUdpSendTest.RequireSegmentation();
            await using var fixture = await SegmentedFixture.StartAsync();
            var bindings = fixture.Bindings();
            var calls = 0;

            foreach (var binding in bindings)
                SegmentField()
                    .SetValue(
                        binding,
                        new Func<SocketAddress, ReadOnlySpan<byte>, int, SocketError>((_, _, _) =>
                        {
                            Interlocked.Increment(ref calls);
                            return SocketError.OperationNotSupported;
                        })
                    );

            var segmented = Counter(bindings, "SegmentedSends");
            await fixture.DownloadTwiceAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(Counter(bindings, "SegmentFallbacks"), Is.EqualTo(1));
                Assert.That(Counter(bindings, "SegmentedSends"), Is.EqualTo(segmented));
                Assert.That(bindings.Any(binding => SegmentField().GetValue(binding) == null), Is.True);
            }
        }

        private static FieldInfo SegmentField() =>
            (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.EmbedIOQuicListenerBinding")
                ?? throw new AssertionException("Missing EmbedIO.Quic listener binding."))
            .GetField("_sendSegments", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertionException("Missing segmented send.");

        private static long Counter(IEnumerable<object> bindings, string name) =>
            bindings.Sum(binding => (long)(binding
                .GetType()
                .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(binding)
                ?? throw new AssertionException("Missing counter: " + name)));

        // A server whose /large is SegmentedBodyLength random bytes, and a client already
        // connected to it over HTTP/3.
        private sealed class SegmentedFixture : IAsyncDisposable
        {
            private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate;
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(60));
            private readonly System.Net.Http.HttpClient _client;
            private readonly string _prefix;
            private readonly byte[] _body = new byte[SegmentedBodyLength];
            private readonly Task _running;

            private SegmentedFixture()
            {
                new Random(548).NextBytes(_body);
                _certificate = Certificate();
                _prefix = Prefix();
                _server = new WebServer(o => o
                    .WithUrlPrefix(_prefix)
                    .WithMode(HttpListenerMode.EmbedIOHttp3)
                    .WithCertificate(_certificate))
                    .WithAction("/ready", HttpVerbs.Get, context => context.SendStringAsync("ready", "text/plain", WebServer.Utf8NoBomEncoding))
                    .WithAction("/large", HttpVerbs.Get, async context =>
                    {
                        context.Response.ContentType = "application/octet-stream";
                        context.Response.ContentLength64 = _body.Length;
                        await context.Response.OutputStream.WriteAsync(_body, context.CancellationToken);
                    });
                _running = _server.RunAsync(_stop.Token);
                _client = Client(_certificate);
            }

            internal static async Task<SegmentedFixture> StartAsync()
            {
                var fixture = new SegmentedFixture();

                try
                {
                    Assert.That(await fixture._client.GetStringAsync(fixture._prefix + "ready", fixture._stop.Token), Is.EqualTo("ready"));
                    return fixture;
                }

                catch
                {
                    await fixture.DisposeAsync();
                    throw;
                }
            }

            internal List<object> Bindings()
            {
                var session = DatagramFixtureField(_server.Listener, "_session");
                var bindings = ((IDictionary)DatagramFixtureField(session, "_listeners")).Keys.Cast<object>().ToList();
                Assert.That(bindings, Is.Not.Empty);
                Assert.That(bindings.Select(binding => binding.GetType().Name), Is.All.EqualTo("EmbedIOQuicListenerBinding"));
                return bindings;
            }

            internal async Task DownloadTwiceAsync()
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var received = await _client.GetByteArrayAsync(_prefix + "large", _stop.Token);
                    Assert.That(received.AsSpan().SequenceEqual(_body), Is.True, "The response arrived intact.");
                }
            }

            public async ValueTask DisposeAsync()
            {
                _client.Dispose();
                await _stop.CancelAsync();

                try
                {
                    await _running.WaitAsync(TimeSpan.FromSeconds(10));
                }

                finally
                {
                    _server.Dispose();
                    _stop.Dispose();
                    _certificate.Dispose();
                }
            }
        }
    }
}
