using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TransportAllocationRegressionTest
    {
        private static readonly Assembly Core = typeof(WebServer).Assembly;
        private static readonly Type ConnectionType = Core.GetType("EmbedIO.Net.Internal.HttpConnection", true)!;

        [TestCase(0, false)]
        [TestCase(125, false)]
        [TestCase(126, false)]
        [TestCase(65535, false)]
        [TestCase(65536, false)]
        [TestCase(0, true)]
        [TestCase(125, true)]
        [TestCase(126, true)]
        [TestCase(65535, true)]
        [TestCase(65536, true)]
        public void WebSocketFramesPreserveWireBytesAtLengthBoundaries(int length, bool masked)
        {
            var frameType = Core.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true)!;
            var fin = Core.GetType("EmbedIO.WebSockets.Internal.Fin", true)!;
            var opcode = Core.GetType("EmbedIO.WebSockets.Opcode", true)!;
            var data = new byte[length];
            new Random(42).NextBytes(data);
            var frame = Activator.CreateInstance(frameType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { Enum.Parse(fin, "Final"), Enum.Parse(opcode, "Binary"), (object)data, false }, null)!;
            var key = new byte[] { 1, 2, 3, 4 };
            if (masked)
            {
                frameType.GetProperty("Mask")!.SetValue(frame, Enum.Parse(Core.GetType("EmbedIO.WebSockets.Internal.Mask", true)!, "On"));
                frameType.GetProperty("MaskingKey")!.SetValue(frame, key);
            }
            var result = (byte[])frameType.GetMethod("ToArray")!.Invoke(frame, null)!;
            using var expected = new MemoryStream();
            expected.WriteByte(0x82);
            expected.WriteByte((byte)((masked ? 0x80 : 0) | (length < 126 ? length : length <= 65535 ? 126 : 127)));
            if (length >= 126)
            {
                var extended = length <= 65535 ? BitConverter.GetBytes((ushort)length) : BitConverter.GetBytes((ulong)length);
                if (BitConverter.IsLittleEndian) Array.Reverse(extended);
                expected.Write(extended);
            }
            if (masked) expected.Write(key);
            expected.Write(data);
            Assert.That(result, Is.EqualTo(expected.ToArray()));
        }

        [TestCase(0, 4)]
        [TestCase(2, 4)]
        [TestCase(4, 4)]
        [TestCase(5, 4)]
        [TestCase(16384, 4096)]
        public async Task FrameReadsPreserveShortReadAndEofBehavior(int count, int bufferSize)
        {
            var read = Core.GetType("EmbedIO.Internal.StreamExtensions", true)!
                .GetMethod("ReadBytesAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<Stream, int, int, Task<byte[]>>>();
            using var stream = new ShortReadStream(Encoding.ASCII.GetBytes("abcdef"));
            Assert.That(await read(stream, count, bufferSize), Is.EqualTo(Encoding.ASCII.GetBytes("abcdef").Take(count).ToArray()));
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(8192)]
        public void FragmentedHttpInputPreservesBufferedBodyAndIgnoresUnusedCapacity(int chunkSize)
        {
            // Initialize parser state without opening a listener or a socket.
            var connection = NewConnection(Stream.Null);
            var buffered = (MemoryStream)Field("_ms").GetValue(connection)!;
            buffered.Capacity = 8192;
            Array.Fill(buffered.GetBuffer(), (byte)'X');
            var bytes = Encoding.ASCII.GetBytes("POST /items?q=42 HTTP/1.1\r\nHost: localhost\r\nContent-Length: 6\r\n\r\nabcdef");
            var process = ConnectionType.GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var position = 0;
            var complete = false;
            while (position < bytes.Length && !complete)
            {
                var count = Math.Min(chunkSize, bytes.Length - position);
                buffered.Write(bytes, position, count);
                position += count;
                complete = (bool)process.Invoke(connection, new object[] { buffered })!;
            }
            Assert.That(complete, Is.True);
            using var remainder = new MemoryStream(bytes, position, bytes.Length - position);
            Field("<Stream>k__BackingField").SetValue(connection, remainder);
            var context = (IHttpContext)Field("_context").GetValue(connection)!;
            Assert.That(context.Request.RawUrl, Is.EqualTo("/items?q=42"));
            using var body = new MemoryStream();
            context.Request.InputStream.CopyTo(body);
            Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("abcdef"));
        }

        [TestCase(0, false)]
        [TestCase(12, false)]
        [TestCase(20000, false)]
        [TestCase(0, true)]
        [TestCase(12, true)]
        [TestCase(20000, true)]
        public void HttpOutputPreservesHeadersBodyAndChunkFraming(int length, bool chunked)
        {
            using var output = new MemoryStream();
            var connection = NewConnection(output);
            var context = (IHttpContext)Field("_context").GetValue(connection)!;
            context.Response.SendChunked = chunked;
            if (!chunked) context.Response.ContentLength64 = length;
            var payload = Encoding.ASCII.GetBytes(new string('a', length));
            var stream = context.Response.OutputStream;
            if (length > 0) stream.Write(payload, 0, length);
            stream.Dispose();
            var wire = Encoding.ASCII.GetString(output.ToArray());
            Assert.That(wire, Does.StartWith("HTTP/1.1 200 OK\r\n"));
            var body = wire.Substring(wire.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
            var expected = chunked && length > 0
                ? (length == 0 ? "" : $"{length:x}\r\n{new string('a', length)}\r\n") + "0\r\n\r\n"
                : new string('a', length);
            Assert.That(body, Is.EqualTo(expected));
        }


        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1015)]
        [TestCase(1016)]
        [TestCase(1017)]
        [TestCase(2032)]
        [TestCase(4096)]
        public async Task WebSocketFragmentationAndReassemblyPreservePayload(int length)
        {
            var data = new byte[length];
            new Random(42).NextBytes(data);
            var opcode = Core.GetType("EmbedIO.WebSockets.Opcode", true)!;
            var streamType = Core.GetType("EmbedIO.WebSockets.Internal.WebSocketStream", true)!;
            using var stream = (IDisposable)Activator.CreateInstance(streamType, new[] { (object)data, Enum.Parse(opcode, "Binary") })!;
            var socketType = Core.GetType("EmbedIO.WebSockets.Internal.WebSocket", true)!;
            var socket = RuntimeHelpers.GetUninitializedObject(socketType);
            var queueField = socketType.GetField("_messageEventQueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var queue = Activator.CreateInstance(queueField.FieldType)!;
            queueField.SetValue(socket, queue);
            var frames = (System.Collections.IEnumerable)streamType.GetMethod("GetFrames")!.Invoke(stream, null)!;
            var count = 0;
            foreach (var frame in frames)
            {
                var frameType = frame.GetType();
                var wire = (byte[])frameType.GetMethod("ToArray")!.Invoke(frame, null)!;
                Assert.That(wire[0] & 0x70, Is.Zero, "No unnegotiated extensions may be emitted.");
                Assert.That(wire[0] & 0x0f, Is.EqualTo(count++ == 0 ? 2 : 0));
                var fragmented = (bool)frameType.GetProperty("IsFragment")!.GetValue(frame)!;
                var handler = socketType.GetMethod(fragmented ? "ProcessFragmentFrame" : "ProcessDataFrame",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)handler.Invoke(socket, new[] { frame })!;
            }
            var args = new object?[] { null };
            Assert.That(queue.GetType().GetMethod("TryDequeue")!.Invoke(queue, args), Is.EqualTo(true));
            Assert.That(args[0]!.GetType().GetProperty("RawData")!.GetValue(args[0]), Is.EqualTo(data));
            Assert.That(queue.GetType().GetProperty("IsEmpty")!.GetValue(queue), Is.EqualTo(true));
            Assert.That(socketType.GetProperty("InContinuation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(socket), Is.EqualTo(false));
        }

        [Test]
        public void WebSocketCompressionRemainsRejected()
        {
            var frame = NewFrame(new byte[] { 42 });
            var frameType = frame.GetType();
            frameType.GetProperty("Mask")!.SetValue(frame, Enum.Parse(Core.GetType("EmbedIO.WebSockets.Internal.Mask", true)!, "On"));
            frameType.GetProperty("Rsv1")!.SetValue(frame, Enum.Parse(Core.GetType("EmbedIO.WebSockets.Internal.Rsv", true)!, "On"));
            var socket = RuntimeHelpers.GetUninitializedObject(Core.GetType("EmbedIO.WebSockets.Internal.WebSocket", true)!);
            var error = Assert.Throws<TargetInvocationException>(() =>
                frameType.GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(frame, new[] { socket }))!;
            Assert.That(error.InnerException, Is.TypeOf<EmbedIO.WebSockets.WebSocketException>());
            Assert.That(error.InnerException!.Message, Does.Contain("without any agreement"));
        }

        [Test]
        public void WebSocketMessageDataRemainsIndependentOfFramePayload()
        {
            var data = new byte[] { 1, 2, 3 };
            var frame = NewFrame(data);
            var eventType = Core.GetType("EmbedIO.WebSockets.Internal.MessageEventArgs", true)!;
            var message = Activator.CreateInstance(eventType, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { frame }, null)!;
            var raw = (byte[])eventType.GetProperty("RawData")!.GetValue(message)!;
            data[0] = 9;
            Assert.That(raw, Is.EqualTo(new byte[] { 1, 2, 3 }));
            raw[1] = 8;
            Assert.That(data[1], Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CachedRoutesPreserveNormalizationValidationAndCacheClearing(bool baseRoute)
        {
            var first = EmbedIO.Routing.RouteMatcher.Parse("/perf/{id}", baseRoute);
            Assert.That(EmbedIO.Routing.RouteMatcher.Parse(first.Route, baseRoute), Is.SameAs(first));
            Assert.That(EmbedIO.Routing.RouteMatcher.Parse("//perf//{id}//", baseRoute), Is.SameAs(first));
            Assert.Throws<ArgumentNullException>(() => EmbedIO.Routing.RouteMatcher.Parse(null!, baseRoute));
            Assert.That(EmbedIO.Routing.RouteMatcher.TryParse(null!, baseRoute, out var missing), Is.False);
            Assert.That(missing, Is.Null);
            Assert.That(EmbedIO.Routing.RouteMatcher.TryParse("/perf/{id}/{id}", baseRoute, out var invalid), Is.False);
            Assert.That(invalid, Is.Null);
            EmbedIO.Routing.RouteMatcher.ClearCache();
            Assert.That(EmbedIO.Routing.RouteMatcher.Parse(first.Route, baseRoute), Is.Not.SameAs(first));
        }

        private static object NewFrame(byte[] data) =>
            Activator.CreateInstance(Core.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true)!,
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] {
                    Enum.Parse(Core.GetType("EmbedIO.WebSockets.Internal.Fin", true)!, "Final"),
                    Enum.Parse(Core.GetType("EmbedIO.WebSockets.Opcode", true)!, "Binary"),
                    (object)data, false
                }, null)!;

        private static object NewConnection(Stream transport)
        {
            var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
            Field("<Stream>k__BackingField").SetValue(connection, transport);
            ConnectionType.GetMethod("Init", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(connection, null);
            return connection;
        }
        private static FieldInfo Field(string name) => ConnectionType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

        private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
        {
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
        }
    }
}
