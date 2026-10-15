using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2HuffmanTest
    {
        private static readonly Type Codec = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackHuffman", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static readonly Func<byte[], int, int, int, string> Decode = (Codec.GetMethod("Decode", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).CreateDelegate<Func<byte[], int, int, int, string>>();
        private static readonly Action<Stream, byte[]> Encode = (Codec.GetMethod("Encode", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).CreateDelegate<Action<Stream, byte[]>>();

        [TestCase("www.example.com", "f1e3c2e5f23a6ba0ab90f4ff")]
        [TestCase("no-cache", "a8eb10649cbf")]
        [TestCase("custom-key", "25a849e95ba97d7f")]
        [TestCase("custom-value", "25a849e95bb8e8b4bf")]
        [TestCase("302", "6402")]
        [TestCase("private", "aec3771a4b")]
        [TestCase("", "")]
        public void PublishedHuffmanVectors(string plain, string hex)
        {
            var wire = Convert.FromHexString(hex);
            Assert.That(Decode(wire, 0, wire.Length, 1024), Is.EqualTo(plain));
            using var encoded = new MemoryStream();
            Encode(encoded, Encoding.ASCII.GetBytes(plain));
            Assert.That(encoded.ToArray(), Is.EqualTo(wire));
        }

        [TestCase("ff")]
        [TestCase("ffff")]
        [TestCase("ffffff")]
        [TestCase("ffffffff")]
        [TestCase("18")]
        [TestCase("00")]
        public void InvalidPaddingAndEosAreRejected(string hex)
        {
            var wire = Convert.FromHexString(hex);
            Assert.That(() => Decode(wire, 0, wire.Length, 1024), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void EveryOctetAndDeterministicMixedSequencesRoundTrip()
        {
            var random = new Random(7541);
            for (var round = 0; round < 32; round++)
            {
                var bytes = round == 0 ? Enumerable.Range(0, 256).Select(i => (byte)i).ToArray() : new byte[random.Next(1, 4096)];
                if (round != 0) random.NextBytes(bytes);
                using var encoded = new MemoryStream();
                Encode(encoded, bytes);
                var wire = encoded.ToArray();
                Assert.That(Decode(wire, 0, wire.Length, bytes.Length), Is.EqualTo(new string(bytes.Select(b => (char)b).ToArray())));
            }
        }

        [Test]
        public void DecodedExpansionIsBoundedAndInputRangeIsRespected()
        {
            var plain = new string('a', 64);
            using var encoded = new MemoryStream();
            encoded.WriteByte(0xff);
            Encode(encoded, Encoding.ASCII.GetBytes(plain));
            var count = (int)encoded.Length - 1;
            encoded.WriteByte(0xff);
            var wire = encoded.ToArray();
            Assert.That(Decode(wire, 1, count, 64), Is.EqualTo(plain));
            Assert.That(() => Decode(wire, 1, count, 63), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => Decode(wire, 1, count, 0), Throws.TypeOf<InvalidDataException>());
            Assert.That(Decode(wire, 1, 0, 0), Is.Empty);
            Assert.That(() => Decode(wire, -1, count, 64), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => Decode(wire, 1, int.MaxValue, 64), Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}
