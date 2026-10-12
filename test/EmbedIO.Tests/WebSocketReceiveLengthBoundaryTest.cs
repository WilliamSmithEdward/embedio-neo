using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class WebSocketReceiveLengthBoundaryTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class HeaderOnlyInput : MemoryStream
        {
            private readonly bool _eofAfterHeader;
            internal int Reads;
            internal bool Disposed;
            internal HeaderOnlyInput(ulong length, bool eofAfterHeader) : base(Header(length), false)
                => _eofAfterHeader = eofAfterHeader;

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Reads++;
                if (Reads > 1 && !_eofAfterHeader)
                    throw new AssertionException("Rejected header attempted to read a mask or payload.");
                return base.ReadAsync(buffer, offset, count, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Disposed = true;
                base.Dispose(disposing);
            }
        }

        private static byte[] Header(ulong length)
        {
            var header = new byte[10];
            header[0] = 0x82;
            header[1] = 0xff;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(2), length);
            return header;
        }

        private static object Create(string name, params object?[] arguments)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal." + name)
                ?? throw new AssertionException("Missing frame/budget type: " + name);
            return Activator.CreateInstance(type, Hidden | BindingFlags.Public, null, arguments, null)
                ?? throw new AssertionException("Missing constructor: " + name);
        }

        private static object Invoke(object instance, string name, params object?[] arguments)
            => (instance.GetType().GetMethod(name, Hidden)
                ?? throw new AssertionException("Missing method: " + name)).Invoke(instance, arguments)
                ?? throw new AssertionException("Missing result: " + name);

        private static void AssertEmpty(object budget)
        {
            var snapshot = budget.GetType().GetProperty("Outstanding", Hidden)?.GetValue(budget)
                ?? throw new AssertionException("Missing outstanding snapshot.");
            Assert.That(snapshot.GetType().GetProperty("Messages", Hidden)?.GetValue(snapshot), Is.EqualTo(0L));
            Assert.That(snapshot.GetType().GetProperty("Bytes", Hidden)?.GetValue(snapshot), Is.EqualTo(0L));
        }

        [TestCase(65536UL, CloseStatusCode.PolicyViolation)]
        [TestCase(70000UL, CloseStatusCode.PolicyViolation)]
        [TestCase(2147483647UL, CloseStatusCode.PolicyViolation)]
        [TestCase(2147483648UL, CloseStatusCode.TooBig)]
        [TestCase(9223372036854775807UL, CloseStatusCode.TooBig)]
        [TestCase(9223372036854775808UL, CloseStatusCode.ProtocolError)]
        public async Task HeaderOnly64BitLengthRejectsBeforeMaskOrPayloadRead(ulong length, CloseStatusCode expected)
        {
            await CheckAsync(length, expected, false);
        }

        [Test]
        public async Task RejectedMaximumRepresentableLengthSettlesItsSkipOnOwnedInputEof()
        {
            await CheckAsync(int.MaxValue, CloseStatusCode.PolicyViolation, true);
        }

        private static async Task CheckAsync(ulong length, CloseStatusCode expected, bool skipEof)
        {
            using var input = new HeaderOnlyInput(length, skipEof);
            using var socketInput = new MemoryStream();
            var socket = Create("WebSocket", socketInput, (Action)(() => { }));
            var budget = Create("WebSocketDeliveryBudget", Create("WebSocketDeliveryLimits", 1, 4L, 1, 4L));
            var connection = Invoke(budget, "CreateConnection");
            using var reader = Create("WebSocketFrameStream", input, false, connection) as IDisposable
                ?? throw new AssertionException("Missing disposable frame reader.");
            try
            {
                var operation = Invoke(reader, "ReadFrameAsync", socket) as Task
                    ?? throw new AssertionException("Missing frame read operation.");
                var error = await Assert.ThrowsAsync<EmbedIO.WebSockets.WebSocketException>(async () =>
                    await operation.WaitAsync(TimeSpan.FromSeconds(3)))
                    ?? throw new AssertionException("Expected the frame rejection error.");
                Assert.That(error.Code, Is.EqualTo(expected));
                Assert.That(input.Reads, Is.EqualTo(1));
                Assert.That(input.Position, Is.EqualTo(10));
                AssertEmpty(budget);

                if (skipEof)
                {
                    var skip = Invoke(reader, "ReadFrameAsync", socket) as Task
                        ?? throw new AssertionException("Missing skip read.");
                    var truncated = await Assert.ThrowsAsync<EmbedIO.WebSockets.WebSocketException>(async () =>
                        await skip.WaitAsync(TimeSpan.FromSeconds(3)))
                        ?? throw new AssertionException("Expected the truncated rejected payload error.");
                    Assert.That(truncated.Code, Is.EqualTo(CloseStatusCode.Abnormal));
                    Assert.That(input.Reads, Is.EqualTo(2));
                    AssertEmpty(budget);
                }
                reader.Dispose();
                Assert.That(input.Disposed, Is.False, "Frame retirement cannot dispose its borrowed source.");
            }
            finally
            {
                var close = Invoke(socket, "InternalCloseAsync", null, false, false, CancellationToken.None) as Task
                    ?? throw new AssertionException("Missing socket cleanup.");
                await close;
                ((IDisposable)socket).Dispose();
            }
        }
    }
}
