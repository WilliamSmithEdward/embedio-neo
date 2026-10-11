using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2PrefixIntegerTest
    {
        private delegate int ReadInteger(byte[] source, ref int offset, int end, int prefixBits);
        private static readonly Type Codec = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.PrefixInteger", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static readonly ReadInteger Read = (Codec.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).CreateDelegate<ReadInteger>();
        private static readonly Action<Stream, int, int, byte> Write = (Codec.GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).CreateDelegate<Action<Stream, int, int, byte>>();

        [TestCase(10, 5, "0a")]
        [TestCase(1337, 5, "1f9a0a")]
        [TestCase(42, 8, "2a")]
        [TestCase(31, 5, "1f00")]
        public void KnownWireIntegers(int value, int bits, string hex)
        {
            var wire = Convert.FromHexString(hex);
            var position = 0;
            Assert.That(Read(wire, ref position, wire.Length, bits), Is.EqualTo(value));
            Assert.That(position, Is.EqualTo(wire.Length));
            using var encoded = new MemoryStream();
            Write(encoded, value, bits, 0);
            Assert.That(encoded.ToArray(), Is.EqualTo(wire));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        public void BoundariesFlagsAndFollowingBytesRemainIndependent(int bits)
        {
            var mask = (1 << bits) - 1;
            var flags = (byte)(255 ^ mask);
            foreach (var value in new[] { 0, mask - 1, mask, mask + 1, 127, 128, 255, 16384, int.MaxValue })
            {
                using var encoded = new MemoryStream();
                encoded.WriteByte(99);
                Write(encoded, value, bits, flags);
                var boundary = (int)encoded.Length;
                encoded.WriteByte(77);
                var wire = encoded.ToArray();
                var position = 1;
                Assert.That(Read(wire, ref position, boundary, bits), Is.EqualTo(value));
                Assert.That(position, Is.EqualTo(boundary));
                Assert.That(wire[1] & flags, Is.EqualTo(flags));
                Assert.That(wire[position], Is.EqualTo(77));
                for (var end = 1; end < boundary; end++)
                {
                    position = 1;
                    var limit = end;
                    Assert.That(() => Read(wire, ref position, limit, bits), Throws.TypeOf<EndOfStreamException>());
                    Assert.That(position, Is.LessThanOrEqualTo(limit));
                }
            }
        }

        [TestCase("1fffffffff08")]
        [TestCase("1f808080808000")]
        [TestCase("1fffffffffff7f")]
        public void ExcessiveIntegerIsRejectedWithoutReadingItsSuccessor(string hex)
        {
            var wire = Convert.FromHexString(hex + "55");
            var position = 0;
            Assert.That(() => Read(wire, ref position, wire.Length - 1, 5), Throws.TypeOf<InvalidDataException>());
            Assert.That(position, Is.LessThanOrEqualTo(6));
            Assert.That(wire[wire.Length - 1], Is.EqualTo(0x55));
        }

        [Test]
        public void PermittedNonminimalIntegerDoesNotLoseSynchronization()
        {
            var wire = Convert.FromHexString("1f800055");
            var position = 0;
            Assert.That(Read(wire, ref position, wire.Length, 5), Is.EqualTo(31));
            Assert.That(position, Is.EqualTo(3));
        }

        [Test]
        public void InvalidEncodingArgumentsDoNotWriteOutput()
        {
            using var output = new MemoryStream();
            Assert.That(() => Write(output, -1, 5, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => Write(output, 1, 0, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => Write(output, 1, 9, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => Write(output, 1, 5, 1), Throws.TypeOf<ArgumentException>());
            Assert.That(output.Length, Is.Zero);
        }
    }
}
