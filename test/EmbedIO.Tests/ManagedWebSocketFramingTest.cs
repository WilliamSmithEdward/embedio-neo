using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ManagedWebSocketFramingTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type SocketType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket", true)!;
        private static readonly Type ReaderType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrameStream", true)!;
        private static IEnumerable<TestCaseData> InvalidHeaders()
        {
            yield return new TestCaseData("817F", false, 1002).SetName("UnmaskedBeforeLength");
            yield return new TestCaseData("C1FF", false, 1002).SetName("Rsv1BeforeLength");
            yield return new TestCaseData("A1FF", false, 1002).SetName("Rsv2BeforeLength");
            yield return new TestCaseData("91FF", false, 1002).SetName("Rsv3BeforeLength");
            yield return new TestCaseData("80FF", false, 1002).SetName("UnexpectedContinuationBeforeLength");
            yield return new TestCaseData("81FF", true, 1002).SetName("NewTextDuringFragmentBeforeLength");
            yield return new TestCaseData("82FF", true, 1002).SetName("NewBinaryDuringFragmentBeforeLength");
            yield return new TestCaseData("83FF", false, 1002).SetName("ReservedOpcodeBeforeLength");
            yield return new TestCaseData("0980", false, 1002).SetName("FragmentedPingBeforeMask");
            yield return new TestCaseData("89FE", false, 1002).SetName("LongPingBeforeLength");
            yield return new TestCaseData("81FE007D", false, 1002).SetName("Nonminimal16BitLengthBeforeMask");
            yield return new TestCaseData("81FE0000", false, 1002).SetName("NonminimalEmptyLengthBeforeMask");
            yield return new TestCaseData("81FF000000000000FFFF", false, 1002).SetName("Nonminimal64BitLengthBeforeMask");
            yield return new TestCaseData("81FF8000000000000000", false, 1002).SetName("HighBitLengthBeforeMask");
            yield return new TestCaseData("81FF0000000080000000", false, 1009).SetName("UnrepresentableLengthBeforeMask");
            yield return new TestCaseData("81FF0000000100000000", false, 1009).SetName("WrappingLengthBeforeMask");
        }
        private static async Task<object> Read(Stream stream, bool continuation)
        {
            var socket = RuntimeHelpers.GetUninitializedObject(SocketType);
            GC.SuppressFinalize(socket);
            SocketType.GetProperty("InContinuation", Hidden)!.SetValue(socket, continuation);
            var reader = Activator.CreateInstance(ReaderType, new object[] { stream, false })!;
            var task = (Task)ReaderType.GetMethod("ReadFrameAsync", Hidden)!.Invoke(reader, new[] { socket })!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        [TestCaseSource(nameof(InvalidHeaders))]
        public async Task InvalidHeaderIsRejectedBeforeReadingFurtherBytes(string hex, bool continuation, int code)
        {
            using var stream = new HeaderOnlyStream(Convert.FromHexString(hex));
            var error = await Assert.ThrowsAsync<WebSocketException>(async () => await Read(stream, continuation));
            Assert.That((int)error!.Code, Is.EqualTo(code));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        }
        [TestCase(0, 1, false)]
        [TestCase(125, 2, false)]
        [TestCase(126, 2, false)]
        [TestCase(65535, 2, false)]
        [TestCase(65536, 2, false)]
        [TestCase(7, 0, true)]
        [TestCase(4, 9, true)]
        [TestCase(0, 8, false)]
        public async Task ValidMaskedBoundaryFramesRemainReadable(int length, byte opcode, bool continuation)
        {
            using var stream = new MemoryStream();
            stream.WriteByte((byte)(0x80 | opcode));
            if (length < 126) stream.WriteByte((byte)(0x80 | length));
            else if (length <= ushort.MaxValue)
            {
                stream.WriteByte(0xfe); stream.WriteByte((byte)(length >> 8)); stream.WriteByte((byte)length);
            }
            else
            {
                stream.WriteByte(0xff);
                for (var shift = 56; shift >= 0; shift -= 8) stream.WriteByte((byte)((ulong)length >> shift));
            }
            var mask = new byte[] { 1, 23, 45, 67 };
            stream.Write(mask);
            var expected = new byte[length];
            for (var i = 0; i < length; i++) { expected[i] = (byte)i; stream.WriteByte((byte)(expected[i] ^ mask[i % 4])); }
            stream.Position = 0;
            var frame = await Read(stream, continuation);
            var payload = frame.GetType().GetProperty("PayloadData", Hidden | BindingFlags.Public)!.GetValue(frame)!;
            var bytes = (byte[])payload.GetType().GetMethod("ToArray", Hidden)!.Invoke(payload, null)!;
            Assert.That(bytes, Is.EqualTo(expected));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        }

        private static byte[] CloseWire(byte[] payload)
        {
            var wire = new byte[6 + payload.Length]; wire[0] = 0x88; wire[1] = (byte)(0x80 | payload.Length);
            payload.CopyTo(wire, 6); // A zero masking key is valid and keeps fixtures readable.
            return wire;
        }
        private static IEnumerable<TestCaseData> InvalidClosePayloads()
        {
            yield return new TestCaseData(new byte[] { 3 }, 1002).SetName("OneByteClosePayload");
            foreach (var code in new[] { 0, 999, 1004, 1005, 1006, 1015, 1016, 2999, 5000, 65535 })
                yield return new TestCaseData(new byte[] { (byte)(code >> 8), (byte)code }, 1002).SetName("InvalidCloseCode" + code);
            foreach (var hex in new[] { "80", "C080", "EDA080", "F4908080", "E282", "FF" })
            {
                var invalid = Convert.FromHexString(hex);
                var payload = new byte[2 + invalid.Length]; payload[0] = 3; payload[1] = 0xe8;
                invalid.CopyTo(payload, 2);
                yield return new TestCaseData(payload, 1007).SetName("InvalidCloseReason" + hex);
            }
        }
        [TestCaseSource(nameof(InvalidClosePayloads))]
        public async Task InvalidClosePayloadNeverReachesCloseHandling(byte[] payload, int code)
        {
            using var stream = new MemoryStream(CloseWire(payload));
            var error = await Assert.ThrowsAsync<WebSocketException>(async () => await Read(stream, false));
            Assert.That((int)error!.Code, Is.EqualTo(code));
        }
        [TestCase(1000)]
        [TestCase(1001)]
        [TestCase(1002)]
        [TestCase(1003)]
        [TestCase(1007)]
        [TestCase(1008)]
        [TestCase(1009)]
        [TestCase(1010)]
        [TestCase(1011)]
        [TestCase(1012)]
        [TestCase(1013)]
        [TestCase(1014)]
        [TestCase(3000)]
        [TestCase(3003)]
        [TestCase(3999)]
        [TestCase(4000)]
        [TestCase(4999)]
        public async Task ValidCloseCodesAndUnicodeReasonRemainReadable(int code)
        {
            var reason = System.Text.Encoding.UTF8.GetBytes("done 世界 \U0001F680");
            var payload = new byte[2 + reason.Length]; payload[0] = (byte)(code >> 8); payload[1] = (byte)code;
            reason.CopyTo(payload, 2);
            using var stream = new MemoryStream(CloseWire(payload));
            Assert.That(await Read(stream, false), Is.Not.Null);
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        }

        private sealed class HeaderOnlyStream : MemoryStream
        {
            internal HeaderOnlyStream(byte[] bytes) : base(bytes) { }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Position == Length) throw new InvalidOperationException("Parser attempted to read beyond the invalid header.");
                return base.ReadAsync(buffer, offset, Math.Min(count, 1), token);
            }
        }
    }
}
