using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class Http3DatagramCodecTest
    {
        private static object Invoke(string method, params object[] arguments)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3DatagramCodec", true)
                ?? throw new AssertionException("Missing datagram codec.");
            try
            {
                return type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, arguments)
                    ?? throw new AssertionException("Missing codec method.");
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        [TestCase(0L)]
        [TestCase(4L)]
        [TestCase(252L)]
        [TestCase(256L)]
        [TestCase(65532L)]
        [TestCase(65536L)]
        [TestCase(4294967292L)]
        [TestCase(4294967296L)]
        [TestCase(4611686018427387900L)]
        public void StreamIdentifierRoundTripsWithoutTouchingPayloadOrAdjacentBytes(long streamId)
        {
            var packet = new byte[16];
            Array.Fill(packet, (byte)0x5a);
            var length = (int)Invoke("WriteHeader", packet, 2, streamId);
            packet[2 + length] = 0xab;
            var args = new object[] { packet, 2, length + 1, 0 };
            Assert.That(Invoke("ReadHeader", args), Is.EqualTo(streamId));
            Assert.That(args[3], Is.EqualTo(2 + length));
            Assert.That(packet[0], Is.EqualTo(0x5a));
            Assert.That(packet[1], Is.EqualTo(0x5a));
            Assert.That(packet[2 + length], Is.EqualTo(0xab));
            Assert.That(packet[3 + length], Is.EqualTo(0x5a));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(8)]
        public void ZeroStreamAndEmptyPayloadAcceptEveryLegalIntegerWidth(int width)
        {
            var packet = new byte[width];
            packet[0] = (byte)(width == 1 ? 0 : width == 2 ? 64 : width == 4 ? 128 : 192);
            var args = new object[] { packet, 0, packet.Length, -1 };
            Assert.That(Invoke("ReadHeader", args), Is.EqualTo(0L));
            Assert.That(args[3], Is.EqualTo(width));
        }

        [TestCase("")]
        [TestCase("40")]
        [TestCase("8000")]
        [TestCase("c0000000")]
        [TestCase("800000")]
        [TestCase("d000000000000000")]
        [TestCase("ffffffffffffffff")]
        public void InvalidWireIdentifiersUseTheDatagramConnectionError(string hex)
        {
            var packet = Convert.FromHexString(hex);
            var error = Assert.Catch<IOException>(() => Invoke("ReadHeader", packet, 0, packet.Length, 0))
                ?? throw new AssertionException("Missing protocol failure.");
            Assert.That(error.GetType().Name, Is.EqualTo("Http3ProtocolException"));
            Assert.That(error.GetType().GetProperty("ErrorCode")?.GetValue(error), Is.EqualTo(0x33L));
        }

        [TestCase(-1L)]
        [TestCase(1L)]
        [TestCase(2L)]
        [TestCase(3L)]
        [TestCase(4611686018427387903L)]
        public void OutgoingIdentifiersMustBeRequestStreams(long streamId)
            => Assert.Throws<ArgumentOutOfRangeException>(() => Invoke("WriteHeader", new byte[8], 0, streamId));

        [Test]
        public void IntegerCannotBorrowBytesOutsideThePacketSlice()
        {
            var packet = new byte[] { 0x40, 0, 0xab };
            var error = Assert.Catch<IOException>(() => Invoke("ReadHeader", packet, 0, 1, 0))
                ?? throw new AssertionException("Missing protocol failure.");
            Assert.That(error.GetType().Name, Is.EqualTo("Http3ProtocolException"));
            Assert.Throws<ArgumentOutOfRangeException>(() => Invoke("ReadHeader", packet, 1, int.MaxValue, 0));
        }

        [Test]
        public void ShortDestinationDoesNotMutateItsContents()
        {
            var packet = new byte[] { 0xab };
            Assert.Throws<ArgumentOutOfRangeException>(() => Invoke("WriteHeader", packet, 0, 256L));
            Assert.That(packet, Is.EqualTo(new byte[] { 0xab }));
        }
        [TestCase(0L, "00")]
        [TestCase(256L, "4040")]
        [TestCase(65536L, "80004000")]
        [TestCase(4294967292L, "bfffffff")]
        [TestCase(4294967296L, "c000000040000000")]
        [TestCase(4611686018427387900L, "cfffffffffffffff")]
        public void StreamIdentifiersMatchExplicitWireVectors(long streamId, string hex)
        {
            var expected = Convert.FromHexString(hex);
            var packet = new byte[expected.Length];
            Assert.That(Invoke("WriteHeader", packet, 0, streamId), Is.EqualTo(expected.Length));
            Assert.That(packet, Is.EqualTo(expected));
            Assert.That(Invoke("ReadHeader", expected, 0, expected.Length, 0), Is.EqualTo(streamId));
        }

    }
}
