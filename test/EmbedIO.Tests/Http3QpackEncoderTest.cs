using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3QpackTableTest
    {
        private static byte[] Encode((string Name, string Value, bool NeverIndexed)[] fields, int encoded = 65536, int decoded = 65536)
        {
            var assembly = typeof(WebServer).Assembly;
            var fieldType = assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new AssertionException("Missing field type.");
            var values = Array.CreateInstance(fieldType, fields.Length);
            for (var i = 0; i < fields.Length; i++)
                values.SetValue(Activator.CreateInstance(fieldType, Hidden, null, new object[] { fields[i].Name, fields[i].Value, fields[i].NeverIndexed }, null), i);
            var encoder = assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoder", true) ?? throw new AssertionException("Missing encoder.");
            try
            {
                return (byte[]?)(encoder.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing encode method.")).Invoke(null, new object[] { values, encoded, decoded })
                    ?? throw new AssertionException("Missing encoded output.");
            }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }

        [TestCase(":method", "GET", "0000D1")]
        [TestCase(":path", "/", "0000C1")]
        [TestCase(":status", "200", "0000D9")]
        [TestCase("x-frame-options", "sameorigin", "0000FF23")]
        public void EncoderUsesStaticExactFields(string name, string value, string expected)
        {
            var wire = Encode(new[] { (name, value, false) });
            Assert.That(Convert.ToHexString(wire), Is.EqualTo(expected));
            Assert.That(Fields(new Table(0).Section(Convert.ToHexString(wire))), Is.EqualTo(new[] { name + ": " + value }));
        }

        [TestCase("authorization")]
        [TestCase("proxy-authorization")]
        [TestCase("cookie")]
        [TestCase("set-cookie")]
        public void EncoderMarksSensitiveNamesNeverIndexed(string name)
        {
            var wire = Encode(new[] { (name, "secret", false) });
            var fields = Decode(new Table(0).Section(Convert.ToHexString(wire))) ?? throw new AssertionException("Blocked stateless encoding.");
            Assert.That(Value(fields.Single(), "NeverIndexed"), Is.True);
        }

        [Test]
        public void ExplicitNeverIndexedPreservedEvenForExactStaticMatch()
        {
            var wire = Encode(new[] { (":method", "GET", true) });
            var fields = Decode(new Table(0).Section(Convert.ToHexString(wire))) ?? throw new AssertionException("Blocked stateless encoding.");
            Assert.That(Value(fields.Single(), "NeverIndexed"), Is.True);
            Assert.That(Value(fields.Single(), "Value"), Is.EqualTo("GET"));
        }

        [Test]
        public void StatelessEncodingPreservesOrderedOctetsAndDuplicateNames()
        {
            var octets = new string(Enumerable.Range(0, 256).Select(i => (char)i).ToArray());
            var expected = new[] { ("custom-key", "custom-value", false), ("x", octets, false), ("custom-key", "", true) };
            var wire = Encode(expected);
            Assert.That(Fields(new Table(0).Section(Convert.ToHexString(wire))),
                Is.EqualTo(expected.Select(f => f.Item1 + ": " + f.Item2).ToArray()));
            Assert.That(wire.Take(2), Is.EqualTo(new byte[2]));
        }

        [TestCase("x\u0100", "v")]
        [TestCase("x", "v\u0100")]
        public void EncoderRejectsNonOctetApplicationStrings(string name, string value)
        {
            Assert.Throws<ArgumentException>(() => Encode(new[] { (name, value, false) }));
        }

        [Test]
        public void EncoderBudgetsAccountForPrefixAndDecodedIndexedFields()
        {
            Assert.That(Encode(Array.Empty<(string, string, bool)>(), 2, 0), Is.EqualTo(new byte[2]));
            Assert.Throws<InvalidDataException>(() => Encode(Array.Empty<(string, string, bool)>(), 1, 0));
            var fields = new[] { (":method", "GET", false) };
            Assert.That(Encode(fields, 3, 42), Has.Length.EqualTo(3));
            Assert.Throws<InvalidDataException>(() => Encode(fields, 2, 42));
            Assert.Throws<InvalidDataException>(() => Encode(fields, 3, 41));
            var literals = new[] { ("x-custom", new string('q', 255), false) };
            var length = Encode(literals).Length;
            Assert.That(Encode(literals, length), Has.Length.EqualTo(length));
            Assert.Throws<InvalidDataException>(() => Encode(literals, length - 1));
        }
    }
}
