using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        private static byte[] RequestBlock(bool post = false, byte? length = null)
        {
            var bytes = new byte[] { post ? (byte)0x83 : (byte)0x82, 0x86, 0x84, 0x01, 9 }.Concat(Encoding.ASCII.GetBytes("localhost"));
            if (length.HasValue) bytes = bytes.Concat(new byte[] { 0x0f, 13, 1, (byte)('0' + length.Value) });
            return bytes.ToArray();
        }
        private static async Task SendWire(NetworkStream stream, byte type, byte flags, int id, byte[] payload, CancellationToken token)
        {
            var bytes = new byte[9 + payload.Length];
            bytes[0] = (byte)(payload.Length >> 16); bytes[1] = (byte)(payload.Length >> 8); bytes[2] = (byte)payload.Length;
            bytes[3] = type; bytes[4] = flags;
            bytes[5] = (byte)(id >> 24); bytes[6] = (byte)(id >> 16); bytes[7] = (byte)(id >> 8); bytes[8] = (byte)id;
            payload.CopyTo(bytes, 9); await stream.WriteAsync(bytes, token);
        }
        private static async Task<(byte Type, byte Flags, int Id, byte[] Payload)> ReceiveWire(NetworkStream stream, CancellationToken token)
        {
            var header = new byte[9]; await stream.ReadExactlyAsync(header, token);
            var payload = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
            await stream.ReadExactlyAsync(payload, token);
            return (header[3], header[4], ((header[5] & 127) << 24) | (header[6] << 16) | (header[7] << 8) | header[8], payload);
        }
        private static async Task<(byte Type, byte Flags, int Id, byte[] Payload)> Until(NetworkStream stream, byte type, int id, CancellationToken token)
        {
            for (var i = 0; i < 128; i++)
            {
                var frame = await ReceiveWire(stream, token);
                if (frame.Type == type && frame.Id == id) return frame;
                Assert.That(frame.Type, Is.Not.EqualTo(7), "Unexpected GOAWAY.");
                Assert.That(frame.Type, Is.Not.EqualTo(3), "Unexpected stream reset.");
            }
            throw new IOException("Expected frame did not arrive.");
        }
        private static async Task RawEcho(object exchange)
        {
            if (Property<string>(Property<object>(exchange, "Request"), "Method") == "GET")
                await Respond(exchange, new byte[] { 1, 2, 3 });
            else
            {
                using var body = new MemoryStream();
                await Property<Stream>(exchange, "InputStream").CopyToAsync(body, Property<CancellationToken>(exchange, "CancellationToken"));
                await Respond(exchange, body.ToArray());
            }
        }
        private static async Task WithRawServer(byte[] settings, Func<NetworkStream, CancellationToken, Task> verify, uint? expectedConnectionError = null, Func<object, Task>? app = null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                using var stream = socket.GetStream();
                var connection = await Result((Task)((Type("Http2Connection").GetMethod("AcceptAsync", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(null, new object[] { stream, stop.Token }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")));
                using var connectionLifetime = (IDisposable)connection;
                var dispatcher = (Activator.CreateInstance(Type("Http2Dispatcher"), Flags, null, new[] { connection }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                using var dispatcherLifetime = (IDisposable)dispatcher;
                var argument = Expression.Parameter(Type("Http2Exchange"));
                var delegateType = typeof(Func<,>).MakeGenericType(Type("Http2Exchange"), typeof(Task));
                Func<object, Task> application = app ?? RawEcho;
                var callback = Expression.Lambda(delegateType, Expression.Invoke(Expression.Constant(application), Expression.Convert(argument, typeof(object))), argument).Compile();
                await (Task)((dispatcher.GetType().GetMethod("RunAsync", Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(dispatcher, new object[] { callback, stop.Token }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            });
            using var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
                using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), stop.Token);
                await SendWire(stream, 4, 0, 0, settings, stop.Token);
                Assert.That((await ReceiveWire(stream, stop.Token)).Type, Is.EqualTo(4));
                var ack = await ReceiveWire(stream, stop.Token);
                Assert.That((ack.Type, ack.Flags), Is.EqualTo(((byte)4, (byte)1)));
                await SendWire(stream, 4, 1, 0, Array.Empty<byte>(), stop.Token);
                try { await verify(stream, stop.Token); }
                finally
                {
                    stop.Cancel();
                    if (expectedConnectionError.HasValue)
                    {
                        var error = await Assert.CatchAsync<IOException>(async () => await server.WaitAsync(TimeSpan.FromSeconds(5)));
                        Assert.That(Property<uint>(((error ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).InnerException ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), "ErrorCode"), Is.EqualTo(expectedConnectionError.Value));
                    }
                    else await server.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally { stop.Cancel(); listener.Stop(); }
        }

        [Test]
        public async Task ZeroStreamWindowDoesNotBlockPingAndUpdateResumesData()
        {
            await WithRawServer(new byte[] { 0, 4, 0, 0, 0, 0 }, async (stream, token) =>
            {
                await SendWire(stream, 1, 5, 1, RequestBlock(), token);
                await Until(stream, 1, 1, token);
                var ping = new byte[8]; ping[7] = 42;
                await SendWire(stream, 6, 0, 0, ping, token);
                var pong = await ReceiveWire(stream, token);
                Assert.That((pong.Type, pong.Flags, pong.Id), Is.EqualTo(((byte)6, (byte)1, 0)));
                Assert.That(pong.Payload, Is.EqualTo(ping));
                await SendWire(stream, 8, 0, 1, new byte[] { 0, 0, 0, 3 }, token);
                var data = await Until(stream, 0, 1, token);
                Assert.That(data.Payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(data.Flags & 1, Is.EqualTo(1));
            });
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        public async Task BodyLengthMismatchResetsOnlyItsStream(int bytes)
        {
            await WithRawServer(Array.Empty<byte>(), async (stream, token) =>
            {
                await SendWire(stream, 1, 4, 1, RequestBlock(true, 2), token);
                await SendWire(stream, 0, 1, 1, new byte[bytes], token);
                var reset = await Until(stream, 3, 1, token);
                Assert.That(reset.Payload, Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
                await SendWire(stream, 1, 5, 3, RequestBlock(), token);
                Assert.That((await Until(stream, 0, 3, token)).Payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
        }

        [Test]
        public async Task PaddingDoesNotCountAsRequestContent()
        {
            await WithRawServer(Array.Empty<byte>(), async (stream, token) =>
            {
                await SendWire(stream, 1, 4, 1, RequestBlock(true, 1), token);
                await SendWire(stream, 0, 9, 1, new byte[] { 2, 42, 0, 0 }, token);
                Assert.That((await Until(stream, 0, 1, token)).Payload, Is.EqualTo(new byte[] { 42 }));
            });
        }

        [Test]
        public async Task ZeroWindowUpdateIsScopedToTheStream()
        {
            await WithRawServer(new byte[] { 0, 4, 0, 0, 0, 0 }, async (stream, token) =>
            {
                await SendWire(stream, 1, 5, 1, RequestBlock(), token); await Until(stream, 1, 1, token);
                await SendWire(stream, 8, 0, 1, new byte[4], token);
                Assert.That((await Until(stream, 3, 1, token)).Payload, Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
                await SendWire(stream, 1, 5, 3, RequestBlock(), token); await Until(stream, 1, 3, token);
                await SendWire(stream, 8, 0, 3, new byte[] { 0, 0, 0, 3 }, token);
                Assert.That((await Until(stream, 0, 3, token)).Payload.Length, Is.EqualTo(3));
            });
        }

        [Test]
        public async Task ZeroConnectionWindowUpdateSendsProtocolGoaway()
        {
            await WithRawServer(Array.Empty<byte>(), async (stream, token) =>
            {
                await SendWire(stream, 8, 0, 0, new byte[4], token);
                var goaway = await Until(stream, 7, 0, token);
                Assert.That(goaway.Payload[4..8], Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
            }, 1);
        }

        [Test]
        public async Task InvalidHeaderNamesResetOnlyTheMalformedRequest()
        {
            await WithRawServer(Array.Empty<byte>(), async (stream, token) =>
            {
                await SendWire(stream, 1, 5, 1, RequestBlock().Concat(new byte[] { 0, 1, (byte)'X', 1, (byte)'y' }).ToArray(), token);
                Assert.That((await Until(stream, 3, 1, token)).Payload, Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
                await SendWire(stream, 1, 5, 3, RequestBlock(), token);
                Assert.That((await Until(stream, 0, 3, token)).Payload.Length, Is.EqualTo(3));
            });
        }

        [Test]
        public async Task ResetStateIsPublishedBeforeApplicationCancellationCallbacks()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (stream, token) =>
            {
                await SendWire(stream, 1, 4, 1, RequestBlock(true, 2), token);
                await entered.Task.WaitAsync(token);
                await SendWire(stream, 0, 1, 1, new byte[1], token);
                await Until(stream, 3, 1, token);
                Assert.That(await observed.Task.WaitAsync(token), Is.True);
            }, app: async exchange =>
            {
                var state = ((exchange.GetType().GetProperty("State", Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(exchange) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                using var registration = Property<CancellationToken>(exchange, "CancellationToken").Register(() =>
                    observed.TrySetResult((bool)((state.GetType().GetField("Reset", Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(state) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."))));
                entered.TrySetResult();
                using var body = new MemoryStream();
                await Property<Stream>(exchange, "InputStream").CopyToAsync(body, Property<CancellationToken>(exchange, "CancellationToken"));
            });
        }
    }
}
