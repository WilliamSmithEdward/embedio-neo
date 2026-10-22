using System;
using System.Collections;
using System.Diagnostics;
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
        [Test]
        [Platform("Linux")]
        public async Task Http3DatagramDestinationErrorsPreserveHealthyRequests()
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux UDP error-queue fixture.");

            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(o => o
                .WithUrlPrefix(prefix)
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithCertificate(certificate)).WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);

            try
            {
                Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
                var (binding, socket) = ActualHttp3DatagramSocket(server);
                // Linux IP_RECVERR is not represented by SocketOptionName. Enable
                // its native value only in this fixture to deliver a real ICMP error.
                socket.SetRawSocketOption(0, 11, BitConverter.GetBytes(1));

                for (var iteration = 0; iteration < 3; iteration++)
                {
                    var previous = DatagramPathErrors(binding);
                    using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    var unavailable = peer.LocalEndPoint
                        ?? throw new AssertionException("Missing disposable UDP endpoint.");
                    peer.Dispose();
                    await socket.SendToAsync(new byte[40], SocketFlags.None, unavailable, stop.Token);
                    var elapsed = Stopwatch.StartNew();

                    while (
                        DatagramPathErrors(binding) == previous
                        && server.Listener.IsListening
                        && elapsed.Elapsed < TimeSpan.FromSeconds(3)
                    )
                        await Task.Delay(5, stop.Token);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(DatagramPathErrors(binding), Is.GreaterThan(previous));
                        Assert.That(server.Listener.IsListening, Is.True);
                        Assert.That(running.IsCompleted, Is.False);
                    }

                    Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
                    using var fresh = Client(certificate);
                    Assert.That(await fresh.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
                }
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task Http3DisposedDatagramSocketRemainsFatal()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(o => o
                .WithUrlPrefix(prefix)
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithCertificate(certificate)).WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);

            try
            {
                Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
                var (binding, socket) = ActualHttp3DatagramSocket(server);
                socket.Dispose();
                await running.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(server.Listener.IsListening, Is.False);
                    Assert.That(DatagramPathErrors(binding), Is.Zero);
                }
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static (object Binding, Socket Socket) ActualHttp3DatagramSocket(WebServer server)
        {
            var session = DatagramFixtureField(server.Listener, "_session");
            var listeners = (IDictionary)DatagramFixtureField(session, "_listeners");
            var result = listeners.Keys
                .Cast<object>()
                .Select(binding => (Binding: binding, Socket: (Socket)DatagramFixtureField(binding, "_socket")))
                .Single(value => value.Socket.AddressFamily == AddressFamily.InterNetwork);
            Assert.That(result.Binding.GetType().Name, Is.EqualTo("EmbedIOQuicListenerBinding"));
            return result;
        }

        private static object DatagramFixtureField(object owner, string name) =>
            owner
                .GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(owner)
                ?? throw new AssertionException("Missing datagram fixture field: " + name);

        private static long DatagramPathErrors(object binding) =>
            (long)(binding
                .GetType()
                .GetProperty("DatagramPathErrors", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(binding)
                ?? throw new AssertionException("Missing datagram path error counter."));
    }
}
