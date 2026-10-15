using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http3QpackPrefixTest
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
        private static Type Type(string name) => typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3." + name, true)
            ?? throw new AssertionException("Missing QPACK type.");
        private delegate long ReadInteger(byte[] bytes, ref int offset, int end, int bits);
        private static readonly ReadInteger Read = (Type("QpackInteger").GetMethod("Read", Hidden)
            ?? throw new AssertionException("Missing QPACK integer reader.")).CreateDelegate<ReadInteger>();
        private static readonly Action<Stream, long, int, byte> Write = (Type("QpackInteger").GetMethod("Write", Hidden)
            ?? throw new AssertionException("Missing QPACK integer writer.")).CreateDelegate<Action<Stream, long, int, byte>>();
        private static object Call(string method, object[] args)
        {
            try
            {
                return (Type("QpackSectionPrefix").GetMethod(method, Hidden) ?? throw new AssertionException("Missing QPACK prefix method."))
                    .Invoke(null, args) ?? throw new AssertionException("Missing prefix result.");
            }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static long Property(object value, string name) => (long)((value.GetType().GetProperty(name)
            ?? throw new AssertionException("Missing prefix property.")).GetValue(value) ?? throw new AssertionException("Missing property value."));
        private static object Decode(string hex, long capacity, long inserts)
        {
            var bytes = Convert.FromHexString(hex);
            return Call("Read", new object[] { bytes, 0, bytes.Length, capacity, inserts });
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        public void AllPrefixWidthsRetainFullCountersAndFlags(int bits)
        {
            var mask = (1 << bits) - 1;
            var flags = (byte)(255 ^ mask);
            foreach (var value in new long[] { 0, mask - 1, mask, 1337, int.MaxValue, (long)int.MaxValue + 1, (1L << 62) - 1 })
            {
                using var output = new MemoryStream();
                Write(output, value, bits, flags);
                var bytes = output.ToArray();
                Assert.That(bytes.Length, Is.LessThanOrEqualTo(10));
                Assert.That(bytes[0] & flags, Is.EqualTo(flags));
                var offset = 0;
                Assert.That(Read(bytes, ref offset, bytes.Length, bits), Is.EqualTo(value));
                Assert.That(offset, Is.EqualTo(bytes.Length));
            }
        }

        [TestCase("1f9a0a", 1337L)]
        [TestCase("1f00", 31L)]
        [TestCase("1f8000", 31L)]
        public void KnownPrefixVectorsAndRedundantZeroContinuation(string wire, long expected)
        {
            var bytes = Convert.FromHexString(wire);
            var offset = 0;
            Assert.That(Read(bytes, ref offset, bytes.Length, 5), Is.EqualTo(expected));
        }

        [TestCase("ff80808080808080808000")]
        [TestCase("ffffffffffffffffff7f")]
        public void ExcessivePrefixIntegerRejectsWithoutAdvancing(string wire)
        {
            var bytes = Convert.FromHexString(wire);
            var offset = 0;
            Assert.Throws<InvalidDataException>(() => Read(bytes, ref offset, bytes.Length, 8));
            Assert.That(offset, Is.Zero);
        }

        [TestCase("0400", 100L, 10L, 9L, 9L)]
        [TestCase("0482", 100L, 10L, 9L, 6L)]
        [TestCase("0300", 64L, 0L, 2L, 2L)]
        [TestCase("0000", 0L, 0L, 0L, 0L)]
        [TestCase("007f00", 0L, 0L, 0L, 127L)]
        public void PrefixVectorsReconstructWrappedCountsAndSignedBase(string wire, long capacity, long inserts, long required, long baseIndex)
        {
            var prefix = Decode(wire, capacity, inserts);
            Assert.That(Property(prefix, "RequiredInsertCount"), Is.EqualTo(required));
            Assert.That(Property(prefix, "Base"), Is.EqualTo(baseIndex));
        }

        [TestCase("", 100L)]
        [TestCase("00", 100L)]
        [TestCase("ff", 100L)]
        [TestCase("0100", 100L)]
        [TestCase("0700", 100L)]
        [TestCase("0500", 100L)]
        [TestCase("0200", 0L)]
        [TestCase("0080", 100L)]
        [TestCase("0281", 100L)]
        [TestCase("007f", 100L)]
        public void MalformedPrefixIsDecompressionFailure(string wire, long capacity)
        {
            var error = Assert.Catch<IOException>(() => Decode(wire, capacity, 0)) ?? throw new AssertionException("Expected prefix failure.");
            Assert.That(Property(error, "ErrorCode"), Is.EqualTo(0x200));
        }

        [Test]
        public void CountsAcrossManyWrapBoundariesMatchOriginalValues()
        {
            foreach (var capacity in new long[] { 32, 64, 100, 4096 })
            {
                var entries = capacity / 32;
                for (var inserts = 0L; inserts <= 300; inserts += 3)
                {
                    for (var required = Math.Max(1, inserts - entries + 1); required <= inserts + entries; required++)
                    {
                        foreach (var baseIndex in new[] { 0L, required, required + 7 })
                        {
                            var wire = (byte[])Call("Encode", new object[] { required, baseIndex, capacity });
                            var prefix = Call("Read", new object[] { wire, 0, wire.Length, capacity, inserts });
                            Assert.That(Property(prefix, "RequiredInsertCount"), Is.EqualTo(required));
                            Assert.That(Property(prefix, "Base"), Is.EqualTo(baseIndex));
                        }
                    }
                }
            }
        }

        [Test]
        public void PrefixHandlesCountersNear62BitLimitWithoutSignedOverflow()
        {
            const long maximum = (1L << 62) - 1;
            var wire = (byte[])Call("Encode", new object[] { maximum, maximum, maximum });
            var prefix = Call("Read", new object[] { wire, 0, wire.Length, maximum, maximum });
            Assert.That(Property(prefix, "RequiredInsertCount"), Is.EqualTo(maximum));
            Assert.That(Property(prefix, "Base"), Is.EqualTo(maximum));
        }
    }
}
