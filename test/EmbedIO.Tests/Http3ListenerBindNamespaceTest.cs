using System;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public void SingleFamilyUdpReservationDoesNotReserveOtherLoopbackFamily(bool ipv6Occupied)
        {
            Assert.That(Socket.OSSupportsIPv6, Is.True, "This controlled namespace comparison requires actual IPv6 support.");
            using var occupied = NamespaceSocket(ipv6Occupied);
            occupied.Bind(new IPEndPoint(ipv6Occupied ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0));
            var endpoint = occupied.LocalEndPoint as IPEndPoint ?? throw new AssertionException("Missing held UDP endpoint.");
            using var probe = NamespaceSocket(!ipv6Occupied);
            probe.Bind(new IPEndPoint(ipv6Occupied ? IPAddress.Loopback : IPAddress.IPv6Loopback, endpoint.Port));
            TestContext.Out.WriteLine($"UDP namespace control: held={endpoint}; other-family probe={probe.LocalEndPoint}; both sockets remain owned.");
            Assert.That(((IPEndPoint)(probe.LocalEndPoint ?? throw new AssertionException("Missing probe endpoint."))).Port,
                Is.EqualTo(endpoint.Port));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitialLocalhostQuicBindPreservesOccupiedFamilyException(bool ipv6Occupied)
        {
            if (!QuicConnection.IsSupported)
                throw new AssertionException("QUIC availability changed after fixture prerequisite validation.");
            Assert.That(Socket.OSSupportsIPv6, Is.True);
            Assert.That(EmbedIO.Net.EndPointManager.UseIpv6, Is.True,
                "Do not change global family configuration to make this diagnostic pass.");
            using var certificate = Certificate();
            using var occupied = NamespaceSocket(ipv6Occupied);
            occupied.Bind(new IPEndPoint(ipv6Occupied ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0));
            var endpoint = occupied.LocalEndPoint as IPEndPoint ?? throw new AssertionException("Missing occupied endpoint.");
            using var probe = NamespaceSocket(!ipv6Occupied);
            probe.Bind(new IPEndPoint(ipv6Occupied ? IPAddress.Loopback : IPAddress.IPv6Loopback, endpoint.Port));
            // Prove the probe succeeded, then release only that probe as Prefix
            // does. The deliberately occupied family remains held throughout.
            probe.Dispose();
            var prefix = $"https://localhost:{endpoint.Port}/";
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Get, context =>
                {
                    entered.TrySetResult();
                    return context.SendStringAsync("unexpected admission", "text/plain", WebServer.Utf8NoBomEncoding);
                });
            LogDrainStartup("controlled-before-initial-bind", server, prefix, entered.Task);
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            var response = client.GetStringAsync(prefix, stop.Token);
            SocketException? original = null;
            Exception? primary = null;
            try
            {
                try { await WaitForDrainEntryAsync(server, prefix, entered.Task, running, response, stop.Token); }
                catch (SocketException error) { original = error; }
                Assert.That(original, Is.Not.Null, "The held family must produce an actual initial bind conflict.");
                Assert.That(original?.SocketErrorCode, Is.EqualTo(SocketError.AddressAlreadyInUse));
                Assert.That(entered.Task.IsCompleted, Is.False);
                Assert.That(server.Listener.IsListening, Is.False);
                try { await running; }
                catch (SocketException observed)
                {
                    Assert.That(observed, Is.SameAs(original), "Repeated observation must preserve the original exception object.");
                }
                TestContext.Out.WriteLine("Actual controlled initial-bind exception: " + original);
                primary = original;
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                stop.Cancel();
                await FinishDrainOwnersAsync(server, prefix, entered.Task, running, response, primary, expectedResponseFailure: true);
                LogDrainStartup("controlled-after-initial-bind", server, prefix, entered.Task, running, response);
            }
        }

        private static Socket NamespaceSocket(bool ipv6)
        {
            var socket = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (ipv6) socket.DualMode = false;
                socket.ExclusiveAddressUse = true;
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }
    }
}
