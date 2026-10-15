using System;
using System.Collections;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Adler32Test
    {
        private static readonly Func<uint, byte[]?, int, int, uint> Update = CreateUpdater();
        private static Func<uint, byte[]?, int, int, uint> CreateUpdater()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.Adler32") ?? throw new AssertionException("Missing Adler-32 implementation.");
            var method = type.GetMethod("Update", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(uint), typeof(byte[]), typeof(int), typeof(int) }, null)
                ?? throw new AssertionException("Missing array checksum surface.");
            return method.CreateDelegate<Func<uint, byte[]?, int, int, uint>>();
        }
        private static uint Reference(uint checksum, byte[] data, int offset, int count)
        {
            var low = checksum & 65535;
            var high = checksum >> 16;
            for (var index = offset; index < offset + count; index++)
            {
                low = (low + data[index]) % 65521;
                high = (high + low) % 65521;
            }
            return (high << 16) | low;
        }
        public static IEnumerable BoundaryCases()
        {
            foreach (var length in new[] { 0, 1, 15, 16, 17, 31, 32, 255, 5551, 5552, 5553, 65536, 1000000 })
                foreach (var pattern in new[] { "random", "zero", "maximum" })
                    foreach (var chunk in new[] { 1, 17, 65536 })
                        yield return new object[] { length, pattern, chunk };
        }
        [TestCaseSource(nameof(BoundaryCases))]
        public void FragmentedAndOffsetUpdatesMatchIndependentReference(int length, string pattern, int chunk)
        {
            var bytes = new byte[length + 13];
            new Random(20261009).NextBytes(bytes);
            if (pattern == "zero") Array.Clear(bytes, 7, length);
            else if (pattern == "maximum") Array.Fill(bytes, byte.MaxValue, 7, length);
            var expected = Reference(1, bytes, 7, length);
            uint actual = 1;
            for (var index = 7; index < length + 7;)
            {
                var count = Math.Min(chunk, length + 7 - index);
                actual = Update(actual, bytes, index, count);
                index += count;
            }
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(Update(actual, bytes, length + 7, 0), Is.EqualTo(actual));
        }
        [TestCase("", 1U)]
        [TestCase("Wikipedia", 0x11e60398U)]
        public void KnownVectors(string text, uint expected)
        {
            if (Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic") == "0")
                Assert.That(System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated, Is.False);
            var bytes = Encoding.ASCII.GetBytes(text);
            Assert.That(Update(1, bytes, 0, bytes.Length), Is.EqualTo(expected));
        }
        [Test]
        public void MaximumValidAccumulatorsAndSeededCorpusDoNotOverflow()
        {
            var maximum = new byte[5553];
            Array.Fill(maximum, byte.MaxValue);
            const uint seed = 0xfff0fff0;
            Assert.That(Update(seed, maximum, 0, maximum.Length), Is.EqualTo(Reference(seed, maximum, 0, maximum.Length)));
            var random = new Random(20261009);
            for (var sample = 0; sample < 500; sample++)
            {
                var bytes = new byte[random.Next(0, 8193)];
                random.NextBytes(bytes);
                var initial = ((uint)random.Next(65521) << 16) | (uint)random.Next(65521);
                Assert.That(Update(initial, bytes, 0, bytes.Length), Is.EqualTo(Reference(initial, bytes, 0, bytes.Length)));
            }
        }
        [Test]
        public void InvalidArrayBoundsAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => Assert.That(Update(1, null, 0, 0), Is.EqualTo(1U)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Assert.That(Update(1, new byte[16], -1, 1), Is.EqualTo(1U)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Assert.That(Update(1, new byte[16], 0, -1), Is.EqualTo(1U)));
            Assert.Throws<ArgumentOutOfRangeException>(() => Assert.That(Update(1, new byte[16], 1, 16), Is.EqualTo(1U)));
        }
    }
}
