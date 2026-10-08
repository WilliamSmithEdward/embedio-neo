using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpListenerResourceLifecycleTest
    {
        private static readonly Assembly Core = typeof(WebServer).Assembly;
        private static readonly Type ConnectionType = (Core.GetType("EmbedIO.Net.Internal.HttpConnection", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(0, 0)]
        [TestCase(1, 0)]
        [TestCase(1, 1)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        [TestCase(3, 0)]
        [TestCase(3, 2)]
        [TestCase(3, 3)]
        public void RequestBodyStopsAtContentLengthBeforeFollowingRequest(int bodyLength, int bufferedBodyBytes)
        {
            var body = Encoding.ASCII.GetBytes("abc".Substring(0, bodyLength));
            var nextRequest = Encoding.ASCII.GetBytes("GET /next HTTP/1.1\r\nHost: localhost\r\n\r\n");
            var sourceBytes = new byte[body.Length - bufferedBodyBytes + nextRequest.Length];
            Array.Copy(body, bufferedBodyBytes, sourceBytes, 0, body.Length - bufferedBodyBytes);
            Array.Copy(nextRequest, 0, sourceBytes, body.Length - bufferedBodyBytes, nextRequest.Length);
            using var source = new MemoryStream(sourceBytes);
            using var input = NewRequestStream(source, body, 0, bufferedBodyBytes, body.Length);
            var destination = new byte[4096];
            using var received = new MemoryStream();
            int count;
            while ((count = input.Read(destination, 0, destination.Length)) > 0)
                received.Write(destination, 0, count);
            Assert.That(received.ToArray(), Is.EqualTo(body));
            Assert.That(source.Position, Is.EqualTo(body.Length - bufferedBodyBytes));
            Assert.That(source.ReadByte(), Is.EqualTo((int)'G'));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void ZeroCountBodyReadDoesNotTouchTransportOrConsumeBufferedBody(int bufferedBodyBytes)
        {
            using var source = new CountingReadStream(Encoding.ASCII.GetBytes("cd"));
            using var input = NewRequestStream(source, Encoding.ASCII.GetBytes("ab"), 0, bufferedBodyBytes, 4);
            Assert.That(input.Read(Array.Empty<byte>(), 0, 0), Is.Zero);
            Assert.That(source.ReadCalls, Is.Zero);
            var destination = new byte[4];
            Assert.That(input.Read(destination, 0, 1), Is.EqualTo(1));
            Assert.That(destination[0], Is.EqualTo((byte)(bufferedBodyBytes == 0 ? 'c' : 'a')));
        }

        [Test]
        public async Task DisposingConnectionWhileReadFailsCompletesCleanupTask()
        {
            using var source = new ControlledFailingReadStream();
            var connection = NewConnection(source);
            var reading = (Task)((((ConnectionType).GetMethod("BeginReadRequest") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(connection, null)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            Assert.That(source.ReadStarted, Is.True);
            ((IDisposable)connection).Dispose();
            source.FailRead();
            await Assert.DoesNotThrowAsync(async () => await (reading).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(source.Disposed, Is.True);
        }

        [Test]
        public void DisposingConnectionDoesNotDisposeItsOwningListener()
        {
            using var owner = new Net.HttpListener();
            owner.Start(); // No prefixes are needed for this isolated ownership check.
            using var source = new MemoryStream();
            var connection = NewConnection(source);
            Field("_lastListener").SetValue(connection, owner);
            ((IDisposable)connection).Dispose();
            Assert.That(owner.IsListening, Is.True);
        }

        [TestCase(0)]
        [TestCase(2)]
        public void RequestBodyRespectsContentLengthAcrossShortTransportReads(int bufferedBodyBytes)
        {
            var buffer = Encoding.ASCII.GetBytes("ab");
            var sourceBytes = Encoding.ASCII.GetBytes((bufferedBodyBytes == 0 ? "abcd" : "cd") + "NEXT");
            using var source = new ShortReadStream(sourceBytes);
            using var input = NewRequestStream(source, buffer, 0, bufferedBodyBytes, 4);
            using var received = new MemoryStream();
            input.CopyTo(received, 4096);
            Assert.That(Encoding.ASCII.GetString(received.ToArray()), Is.EqualTo("abcd"));
            Assert.That(source.ReadByte(), Is.EqualTo((int)'N'));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void UnknownBodyLengthStillReadsToTransportEnd(int bufferedBodyBytes)
        {
            using var source = new MemoryStream(Encoding.ASCII.GetBytes(bufferedBodyBytes == 0 ? "abcdef" : "cdef"));
            using var input = NewRequestStream(source, Encoding.ASCII.GetBytes("ab"), 0, bufferedBodyBytes, -1);
            using var received = new MemoryStream();
            input.CopyTo(received, 4096);
            Assert.That(Encoding.ASCII.GetString(received.ToArray()), Is.EqualTo("abcdef"));
            Assert.That(source.Position, Is.EqualTo(source.Length));
        }

        [Test]
        public void BufferedBodyDoesNotExposeFollowingBytesOrUseTransport()
        {
            using var source = new CountingReadStream(Encoding.ASCII.GetBytes("transport"));
            using var input = NewRequestStream(source, Encoding.ASCII.GetBytes("abcNEXT"), 0, 7, 3);
            using var received = new MemoryStream();
            input.CopyTo(received, 4096);
            Assert.That(Encoding.ASCII.GetString(received.ToArray()), Is.EqualTo("abc"));
            Assert.That(source.ReadCalls, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(3)]
        public void ZeroCountReadsStillValidateDestinationAndPreserveArgumentErrors(long contentLength)
        {
            using var source = new CountingReadStream(Encoding.ASCII.GetBytes("abc"));
            using var input = NewRequestStream(source, Array.Empty<byte>(), 0, 0, contentLength);
            Assert.That(Assert.Throws<ArgumentNullException>(() => TestObjects.InvalidInput.Invoke((Func<byte[], int, int, int>)input.Read, null, 0, 0)).ParamName, Is.EqualTo("buffer"));
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => _ = input.Read(Array.Empty<byte>(), -1, 0)).ParamName, Is.EqualTo("off"));
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => _ = input.Read(Array.Empty<byte>(), 0, -1)).ParamName, Is.EqualTo("count"));
            Assert.Throws<ArgumentException>(() => _ = input.Read(Array.Empty<byte>(), 1, 0));
            Assert.Throws<ArgumentException>(() => _ = input.Read(new byte[1], 1, 1));
            Assert.That(input.Read(new byte[1], 1, 0), Is.Zero);
            Assert.That(source.ReadCalls, Is.Zero);
        }

        private static Stream NewRequestStream(Stream source, byte[] buffer, int offset, int length, long contentLength)
            => (Stream)(Activator.CreateInstance((Core.GetType("EmbedIO.Net.Internal.RequestStream", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")),
                PrivateInstance, null, new object[] { source, buffer, offset, length, contentLength }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        private static object NewConnection(Stream source)
        {
            var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
            ((ConnectionType).GetField("_connectionSync", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(connection, new object());
            Field("<Stream>k__BackingField").SetValue(connection, source);
            Field("_timer").SetValue(connection, new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite));
            Field("_sTimeout").SetValue(connection, 90000);
            ((ConnectionType).GetMethod("Init", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(connection, null);
            return connection;
        }

        private static FieldInfo Field(string name) => (ConnectionType.GetField(name, PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        private sealed class ShortReadStream(byte[] data) : MemoryStream(data)
        {
            public override int Read(byte[] buffer, int offset, int count)
                => base.Read(buffer, offset, Math.Min(count, 1));
        }

        private sealed class CountingReadStream(byte[] data) : MemoryStream(data)
        {
            public int ReadCalls { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                return base.Read(buffer, offset, count);
            }
        }

        private sealed class ControlledFailingReadStream : MemoryStream
        {
            private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool ReadStarted { get; private set; }
            public bool Disposed { get; private set; }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ReadStarted = true;
                return _read.Task;
            }

            public void FailRead() => _read.SetException(new IOException("Controlled read failure after disposal."));

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
