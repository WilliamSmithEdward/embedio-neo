using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2HpackTest
    {
        private sealed class Decoder
        {
            private static readonly Type Type = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackDecoder", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            private readonly Func<byte[], Array> _decode;
            internal readonly Action<int> Maximum;
            internal Decoder(int limit = 32768)
            {
                var instance = (Activator.CreateInstance(Type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { limit }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                _decode = (Type.GetMethod("Decode", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).CreateDelegate<Func<byte[], Array>>(instance);
                Maximum = (Type.GetMethod("SetMaximumTableSize", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).CreateDelegate<Action<int>>(instance);
            }
            internal Array Raw(string hex) => _decode(Convert.FromHexString(hex));
            internal string[] Read(string hex) => Raw(hex).Cast<object>().Select(field =>
                (field.GetType().GetProperty("Name") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(field) + ": " + (field.GetType().GetProperty("Value") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(field)).ToArray();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PublishedSequentialRequestBlocksMaintainTheirDynamicTable(bool huffman)
        {
            var decoder = new Decoder();
            Assert.That(decoder.Read(huffman ? "828684418cf1e3c2e5f23a6ba0ab90f4ff" : "828684410f7777772e6578616d706c652e636f6d"),
                Is.EqualTo(new[] { ":method: GET", ":scheme: http", ":path: /", ":authority: www.example.com" }));
            Assert.That(decoder.Read(huffman ? "828684be5886a8eb10649cbf" : "828684be58086e6f2d6361636865"),
                Is.EqualTo(new[] { ":method: GET", ":scheme: http", ":path: /", ":authority: www.example.com", "cache-control: no-cache" }));
            Assert.That(decoder.Read(huffman ? "828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf" : "828785bf400a637573746f6d2d6b65790c637573746f6d2d76616c7565"),
                Is.EqualTo(new[] { ":method: GET", ":scheme: https", ":path: /index.html", ":authority: www.example.com", "custom-key: custom-value" }));
        }

        [Test]
        public void NeverIndexedIsPreservedAndLiteralWithoutIndexingDoesNotInsert()
        {
            var decoder = new Decoder();
            var field = (decoder.Raw("100870617373776f726406736563726574").GetValue(0) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That((field.GetType().GetProperty("NeverIndexed") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(field), Is.True);
            Assert.That(decoder.Read("040c2f73616d706c652f70617468"), Is.EqualTo(new[] { ":path: /sample/path" }));
            Assert.That(() => decoder.Read("be"), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void DynamicEvictionUsesOctetSizeAndPreservesNewestEntry()
        {
            var decoder = new Decoder();
            decoder.Maximum(34);
            Assert.That(decoder.Read("3f034001610162"), Is.EqualTo(new[] { "a: b" }));
            Assert.That(decoder.Read("4001630164be"), Is.EqualTo(new[] { "c: d", "c: d" }));
            Assert.That(() => decoder.Read("bf"), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void OversizedInsertionClearsTableButStillEmitsHeader()
        {
            var decoder = new Decoder();
            decoder.Maximum(32);
            Assert.That(decoder.Read("3f014001610162"), Is.EqualTo(new[] { "a: b" }));
            Assert.That(() => decoder.Read("be"), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void MinimumAcknowledgedSizeMustBeAppliedBeforeRestoringCapacity()
        {
            var decoder = new Decoder();
            decoder.Read("4001610162");
            decoder.Maximum(0);
            decoder.Maximum(4096);
            Assert.That(decoder.Read("203fe11f82"), Is.EqualTo(new[] { ":method: GET" }));
            Assert.That(() => decoder.Read("be"), Throws.TypeOf<InvalidDataException>());
            decoder = new Decoder();
            decoder.Maximum(0);
            decoder.Maximum(4096);
            Assert.That(() => decoder.Read("3fe11f82"), Throws.TypeOf<InvalidDataException>());
        }

        [TestCase("")]
        [TestCase("82")]
        public void RequiredSizeUpdateCannotBeOmitted(string wire)
        {
            var decoder = new Decoder();
            decoder.Maximum(0);
            Assert.That(() => decoder.Read(wire), Throws.TypeOf<InvalidDataException>());
        }

        [TestCase("80")]
        [TestCase("be")]
        [TestCase("8220")]
        [TestCase("3fe21f")]
        [TestCase("40016184ffffffff")]
        public void MalformedBlocksPoisonTheConnectionDecoder(string wire)
        {
            var decoder = new Decoder();
            Assert.That(() => decoder.Read(wire), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => decoder.Read("82"), Throws.TypeOf<InvalidDataException>());
        }

        [TestCase("ff")]
        [TestCase("400561")]
        [TestCase("400161")]
        public void TruncatedBlocksAreNotAccepted(string wire)
        {
            var decoder = new Decoder();
            Assert.That(() => decoder.Read(wire), Throws.TypeOf<EndOfStreamException>());
            Assert.That(() => decoder.Read("82"), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void RepeatedIndexedFieldsCountTowardDecodedHeaderLimit()
        {
            Assert.That(new Decoder(84).Read("8282"), Has.Length.EqualTo(2));
            Assert.That(() => new Decoder(84).Read("828282"), Throws.TypeOf<InvalidDataException>());
            Assert.That(new Decoder(0).Read(""), Is.Empty);
            Assert.That(() => new Decoder(0).Read("82"), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => new Decoder(33).Read("4001610162"), Throws.TypeOf<InvalidDataException>());
        }
    }
}
