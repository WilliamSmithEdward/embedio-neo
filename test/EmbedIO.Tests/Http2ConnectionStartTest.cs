using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2ConnectionStartTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type Connection = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2Connection", true)!;
        private static readonly Type PeerType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2PeerSettings", true)!;
        private static readonly byte[] Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
        private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        private static async Task<object> Result(Task task) { await task; return task.GetType().GetProperty("Result")!.GetValue(task)!; }
        private static Task<object> Accept(Stream stream, CancellationToken token) => Result((Task)Connection.GetMethod("AcceptAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { stream, token })!);
        private static byte[] Settings(params (int Id, uint Value)[] values)
        {
            var bytes = new byte[values.Length * 6];
            for (var i = 0; i < values.Length; i++)
            {
                var (id, value) = values[i];
                var offset = i * 6;
                bytes[offset] = (byte)(id >> 8); bytes[offset + 1] = (byte)id;
                bytes[offset + 2] = (byte)(value >> 24); bytes[offset + 3] = (byte)(value >> 16);
                bytes[offset + 4] = (byte)(value >> 8); bytes[offset + 5] = (byte)value;
            }
            return bytes;
        }
        private static byte[] Frame(byte type, byte flags, int id, byte[] payload)
        {
            var bytes = new byte[9 + payload.Length];
            bytes[0] = (byte)(payload.Length >> 16); bytes[1] = (byte)(payload.Length >> 8); bytes[2] = (byte)payload.Length;
            bytes[3] = type; bytes[4] = flags;
            bytes[5] = (byte)(id >> 24); bytes[6] = (byte)(id >> 16); bytes[7] = (byte)(id >> 8); bytes[8] = (byte)id;
            payload.CopyTo(bytes, 9);
            return bytes;
        }
        private static async Task<(byte Type, byte Flags, byte[] Payload)> Receive(Stream stream, CancellationToken token)
        {
            var header = new byte[9];
            await stream.ReadExactlyAsync(header, token);
            var bytes = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
            await stream.ReadExactlyAsync(bytes, token);
            return (header[3], header[4], bytes);
        }
        private static async Task WithConnection(byte[] initial, bool fragment, Func<object, NetworkStream, CancellationToken, Task> verify)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient { NoDelay = true };
                var accepting = listener.AcceptTcpClientAsync(stop.Token);
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
                using var server = await accepting;
                using var serverStream = server.GetStream();
                using var clientStream = client.GetStream();
                var accepted = Accept(serverStream, stop.Token);
                if (fragment) foreach (var b in Preface) await clientStream.WriteAsync(new[] { b }, stop.Token);
                else await clientStream.WriteAsync(Preface, stop.Token);
                await clientStream.WriteAsync(initial, stop.Token);
                var connection = await accepted;
                using var connectionLifetime = (IDisposable)connection;
                await verify(connection, clientStream, stop.Token);
            }
            finally { listener.Stop(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RealTcpStartupSettingsPingAndGoaway(bool fragment)
        {
            await WithConnection(Frame(4, 0, 0, Settings((4, 0), (5, 32768), (65535, uint.MaxValue))), fragment, async (connection, client, token) =>
            {
                var settings = await Receive(client, token);
                Assert.That(settings.Type, Is.EqualTo(4));
                Assert.That(settings.Flags, Is.Zero);
                Assert.That(settings.Payload, Is.EqualTo(Settings((3, 128), (6, 32768))));
                var ack = await Receive(client, token);
                Assert.That((ack.Type, ack.Flags, ack.Payload.Length), Is.EqualTo(((byte)4, (byte)1, 0)));
                var peer = Property<object>(connection, "Peer");
                Assert.That(Property<int>(peer, "InitialWindowSize"), Is.Zero);
                Assert.That(Property<int>(peer, "MaximumFrameSize"), Is.EqualTo(32768));
                async Task Process(byte[] wire)
                {
                    await client.WriteAsync(wire, token);
                    var frame = await Result((Task)Connection.GetMethod("ReadFrameAsync", Flags)!.Invoke(connection, new object[] { token })!);
                    var handled = await (Task<bool>)Connection.GetMethod("ProcessControlAsync", Flags)!.Invoke(connection, new[] { frame, (object)token })!;
                    Assert.That(handled, Is.True);
                }
                var flow = Connection.GetProperty("SendFlow", Flags)!.GetValue(connection)!;
                flow.GetType().GetMethod("Open", Flags)!.Invoke(flow, new object[] { 1 });
                var waiting = (Task<int>)flow.GetType().GetMethod("ReserveAsync", Flags)!.Invoke(flow, new object[] { 1, 16, token })!;
                Assert.That(waiting.IsCompleted, Is.False, "Initial SETTINGS must reach new stream credit.");
                await Process(Frame(4, 0, 0, Settings((4, 8))));
                Assert.That(await waiting.WaitAsync(token), Is.EqualTo(8));
                var windowAck = await Receive(client, token);
                Assert.That((windowAck.Type, windowAck.Flags), Is.EqualTo(((byte)4, (byte)1)));
                await Process(Frame(4, 1, 0, Array.Empty<byte>()));
                var ping = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                await Process(Frame(6, 0, 0, ping));
                var pong = await Receive(client, token);
                Assert.That((pong.Type, pong.Flags), Is.EqualTo(((byte)6, (byte)1)));
                Assert.That(pong.Payload, Is.EqualTo(ping));
                await Process(Frame(7, 0, 0, Convert.FromHexString("8000000300000008")));
                Assert.That(Property<bool>(connection, "PeerSentGoAway"), Is.True);
                Assert.That(Property<int>(connection, "PeerLastStreamId"), Is.EqualTo(3));
                Assert.That(Property<uint>(connection, "PeerErrorCode"), Is.EqualTo(8));
            });
        }

        [TestCase(4, 1, 0, 0, 1)]
        [TestCase(6, 0, 0, 8, 1)]
        [TestCase(4, 0, 1, 0, 1)]
        [TestCase(4, 0, 0, 5, 6)]
        public async Task InvalidInitialFrameIsRejected(byte type, byte flags, int id, int length, int code)
        {
            var error = await Assert.CatchAsync<IOException>(() => WithConnection(Frame(type, flags, id, new byte[length]), false, (_, _, _) => Task.CompletedTask));
            Assert.That(Property<uint>(error!, "ErrorCode"), Is.EqualTo(code));
        }

        [Test]
        public async Task InvalidAndTruncatedPrefacesAreRejected()
        {
            using var wrong = new MemoryStream(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
            var error = await Assert.CatchAsync<IOException>(async () => await Accept(wrong, CancellationToken.None));
            Assert.That(Property<uint>(error!, "ErrorCode"), Is.EqualTo(1));
            using var truncated = new MemoryStream(Preface, 0, 23);
            await Assert.ThatAsync(async () => await Accept(truncated, CancellationToken.None), Throws.TypeOf<EndOfStreamException>());
        }

        [Test]
        public void SettingsAreValidatedBeforeMutationAndAppliedInWireOrder()
        {
            var peer = Activator.CreateInstance(PeerType, true)!;
            var apply = PeerType.GetMethod("Apply", Flags)!.CreateDelegate<Action<byte[], Action<int>, Action<uint>>>(peer);
            var windows = new List<int>();
            var tables = new List<uint>();
            apply(Settings((4, 0), (4, 100), (4, 50), (1, uint.MaxValue), (2, 0)), windows.Add, tables.Add);
            Assert.That(windows, Is.EqualTo(new[] { -65535, 100, -50 }));
            Assert.That(tables, Is.EqualTo(new[] { uint.MaxValue }));
            Assert.That(Property<int>(peer, "InitialWindowSize"), Is.EqualTo(50));
            Assert.That(Property<bool>(peer, "EnablePush"), Is.False);
            Assert.Catch<IOException>(() => apply(Settings((3, 0), (4, 2147483648)), windows.Add, tables.Add));
            Assert.That(Property<uint>(peer, "MaximumConcurrentStreams"), Is.EqualTo(uint.MaxValue));
            Assert.That(windows.Count, Is.EqualTo(3));
        }

        [TestCase(2, 2L, 1)]
        [TestCase(4, 2147483648L, 3)]
        [TestCase(5, 16383L, 1)]
        [TestCase(5, 16777216L, 1)]
        [TestCase(8, 2L, 1)]
        public void InvalidSettingsRetainErrorCode(int id, long value, int code)
        {
            var peer = Activator.CreateInstance(PeerType, true)!;
            var apply = PeerType.GetMethod("Apply", Flags)!.CreateDelegate<Action<byte[], Action<int>, Action<uint>>>(peer);
            var error = Assert.Catch<IOException>(() => apply(Settings((id, (uint)value)), _ => { }, _ => { }));
            Assert.That(Property<uint>(error!, "ErrorCode"), Is.EqualTo(code));
        }

        [Test]
        public void ExtendedConnectCapabilityCannotBeWithdrawn()
        {
            var peer = Activator.CreateInstance(PeerType, true)!;
            var apply = PeerType.GetMethod("Apply", Flags)!.CreateDelegate<Action<byte[], Action<int>, Action<uint>>>(peer);
            apply(Settings((8, 1)), _ => { }, _ => { });
            Assert.Catch<IOException>(() => apply(Settings((8, 0)), _ => { }, _ => { }));
            Assert.That(Property<bool>(peer, "EnableConnectProtocol"), Is.True);
        }
    }
}
