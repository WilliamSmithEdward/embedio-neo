using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The HTTP/3 listener's segmented UDP sends: one call carries consecutive datagrams of
    // one length to one peer, which arrive as separate datagrams.
    [TestFixture]
    public class SegmentedUdpSendTest
    {
        private static readonly Type Sender =
            typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.UdpSegmentSender")
            ?? throw new InvalidOperationException("Missing segmented sender.");

        // A plain receiver gets each segment as its own datagram, from an IPv4 socket, an
        // IPv6 socket, and a dual-mode socket sending to an IPv4 peer at its mapped address.
        [TestCase(AddressFamily.InterNetwork, false)]
        [TestCase(AddressFamily.InterNetworkV6, false)]
        [TestCase(AddressFamily.InterNetworkV6, true)]
        public async Task SegmentsArriveAsSeparateDatagrams(AddressFamily family, bool dualMode)
        {
            RequireSegmentation();
            var receiverFamily = dualMode ? AddressFamily.InterNetwork : family;
            using var receiver = new Socket(receiverFamily, SocketType.Dgram, ProtocolType.Udp);
            using var sender = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            var loopback = receiverFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;

            try
            {
                receiver.Bind(new IPEndPoint(loopback, 0));

                if (dualMode)
                {
                    sender.DualMode = true;
                    sender.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                }

                else sender.Bind(new IPEndPoint(loopback, 0));
            }

            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressNotAvailable)
            {
                Assert.Ignore("This host has no IPv6 loopback.");
            }

            var port = ((IPEndPoint)(receiver.LocalEndPoint ?? throw new AssertionException("Missing endpoint."))).Port;
            var destination = new IPEndPoint(dualMode ? IPAddress.Loopback.MapToIPv6() : loopback, port).Serialize();
            var send = Send(Create(sender) ?? throw new AssertionException("The platform should segment UDP sends."));
            var lengths = new[] { 1200, 1200, 500 };
            var datagrams = lengths.Select((length, index) => Enumerable.Repeat((byte)(index + 1), length).ToArray()).ToArray();

            Assert.That(send(destination, datagrams.SelectMany(datagram => datagram).ToArray(), 1200), Is.EqualTo(SocketError.Success));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[65536];

            foreach (var expected in datagrams)
            {
                var received = await receiver.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
                Assert.That(buffer.AsSpan(0, received).ToArray(), Is.EqualTo(expected));
            }
        }

        // One segmented send carries consecutive datagrams for one peer, each the length of the
        // first but the last, which may be shorter and ends the run, within 64 segments and
        // 65,000 bytes.
        [Test]
        public void RunsGatherConsecutiveEqualDatagramsForOnePeer()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.EmbedIOQuicListenerBinding+SegmentRun")
                ?? throw new AssertionException("Missing segment run.");
            var run = Activator.CreateInstance(type, nonPublic: true) ?? throw new AssertionException("No segment run.");
            var first = new IPEndPoint(IPAddress.Loopback, 4433);
            var second = new IPEndPoint(IPAddress.Loopback, 4434);
            var firstAddress = first.Serialize();
            var secondAddress = second.Serialize();

            bool Add(IPEndPoint peer, SocketAddress address, int length, byte fill = 1) =>
                (bool)(Invoke(run, "TryAdd", peer, address, Enumerable.Repeat(fill, length).ToArray())
                    ?? throw new AssertionException("TryAdd returned nothing."));
            int Count() => (int)(Property(run, "Count") ?? 0);
            bool Full() => (bool)(Property(run, "Full") ?? false);
            ReadOnlyMemory<byte> Datagram(int index) =>
                (ReadOnlyMemory<byte>)(Invoke(run, "Datagram", index) ?? throw new AssertionException("No datagram."));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Add(first, firstAddress, 0), Is.False, "An empty datagram is never gathered.");
                Assert.That(Add(first, firstAddress, 65001), Is.False, "Nor one longer than a segmented send.");
                Assert.That(Add(first, firstAddress, 1200, 1), Is.True);
                Assert.That(Add(new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 4433).Serialize(), 1200, 2), Is.True, "An equal peer joins.");
                Assert.That(Add(second, secondAddress, 1200), Is.False, "Another peer does not.");
                Assert.That(Add(first, firstAddress, 1201), Is.False, "Nor a longer datagram.");
                Assert.That(Full(), Is.False);
                Assert.That(Add(first, firstAddress, 500, 3), Is.True, "A shorter one joins as the last.");
                Assert.That(Full(), Is.True);
                Assert.That(Add(first, firstAddress, 500), Is.False);
                Assert.That(Count(), Is.EqualTo(3));
                Assert.That(Property(run, "Length"), Is.EqualTo(2900));
                Assert.That(Property(run, "SegmentLength"), Is.EqualTo(1200));
                Assert.That(Property(run, "Address"), Is.SameAs(firstAddress));
                Assert.That(Datagram(0).ToArray(), Is.EqualTo(Enumerable.Repeat((byte)1, 1200).ToArray()));
                Assert.That(Datagram(1).ToArray(), Is.EqualTo(Enumerable.Repeat((byte)2, 1200).ToArray()));
                Assert.That(Datagram(2).ToArray(), Is.EqualTo(Enumerable.Repeat((byte)3, 500).ToArray()));
            }

            Invoke(run, "Clear");
            Assert.That(Count(), Is.Zero);

            for (var index = 0; index < 64; index++) Assert.That(Add(second, secondAddress, 100), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Full(), Is.True, "64 segments fill a run.");
                Assert.That(Add(second, secondAddress, 100), Is.False);
            }

            Invoke(run, "Clear");

            for (var index = 0; index < 54; index++) Assert.That(Add(second, secondAddress, 1200), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Full(), Is.True, "64,800 bytes leave no room for another 1,200.");
                Assert.That(Add(second, secondAddress, 1200), Is.False);
                Assert.That(Add(second, secondAddress, 200), Is.False);
            }
        }

        // Linux reports sendmsg failures as errno values; the listener acts on them as the
        // matching SocketError, and on EIO as a device that cannot segment.
        [TestCase(11, SocketError.WouldBlock)]
        [TestCase(105, SocketError.NoBufferSpaceAvailable)]
        [TestCase(90, SocketError.MessageSize)]
        [TestCase(22, SocketError.InvalidArgument)]
        [TestCase(5, SocketError.OperationNotSupported)]
        [TestCase(95, SocketError.OperationNotSupported)]
        [TestCase(92, SocketError.ProtocolOption)]
        [TestCase(104, SocketError.ConnectionReset)]
        [TestCase(111, SocketError.ConnectionRefused)]
        [TestCase(112, SocketError.HostDown)]
        [TestCase(113, SocketError.HostUnreachable)]
        [TestCase(101, SocketError.NetworkUnreachable)]
        [TestCase(1, SocketError.SocketError)]
        public void LinuxErrorsMapToSocketErrors(int error, SocketError expected)
        {
            var map = Sender.GetMethod("LinuxError", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing errno map.");
            Assert.That(map.Invoke(null, [error]), Is.EqualTo(expected));
        }

        // UDP_SEGMENT is in every Linux kernel .NET 10 supports, and UDP_SEND_MSG_SIZE in
        // Windows 10 version 2004 and later.
        internal static void RequireSegmentation()
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;

            Assert.Ignore("This platform does not segment UDP sends.");
        }

        private static object? Create(Socket socket) =>
            (Sender.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing sender factory.")).Invoke(null, [socket]);

        private static Func<SocketAddress, ReadOnlySpan<byte>, int, SocketError> Send(object sender) =>
            (Sender.GetMethod("Send", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing segmented send."))
            .CreateDelegate<Func<SocketAddress, ReadOnlySpan<byte>, int, SocketError>>(sender);

        private static object? Invoke(object owner, string name, params object[] arguments) =>
            (owner
                .GetType()
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing method: " + name)).Invoke(owner, arguments);

        private static object? Property(object owner, string name) =>
            (owner
                .GetType()
                .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing property: " + name)).GetValue(owner);
    }
}
