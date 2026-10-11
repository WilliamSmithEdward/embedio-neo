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
        private static object Referenced((string Name, string Value, bool Never)[] fields, long[] indices, long capacity = 68, int encoded = 65536, int decoded = 65536)
        {
            var assembly = typeof(WebServer).Assembly;
            var fieldType = assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new AssertionException("Missing field.");
            var array = Array.CreateInstance(fieldType, fields.Length);
            for (var i = 0; i < fields.Length; ++i)
                array.SetValue(Activator.CreateInstance(fieldType, Hidden, null, new object[] { fields[i].Name, fields[i].Value, fields[i].Never }, null), i);
            var type = assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoder", true) ?? throw new AssertionException("Missing encoder.");
            try
            {
                return type.GetMethod("EncodeReferenced", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null,
                    new object[] { array, indices, capacity, encoded, decoded }) ?? throw new AssertionException("Missing section.");
            }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static byte[] SectionWire(object section) => (byte[])Value(section, "Wire");
        private static long[] SectionReferences(object section) => (long[])Value(section, "References");

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(17)]
        public void ReferencedSectionWrapsRequiredCountAgainstPeerMaximum(int count)
        {
            var encoder = new EncoderTable(68);
            var decoder = new Table(68);
            for (var i = 0; i < count; ++i)
            {
                decoder.Feed(Convert.ToHexString(EncoderTable.Wire(encoder.Insert("x", ((char)('a' + i)).ToString()))));
                encoder.Feedback("01");
            }
            var value = ((char)('a' + count - 1)).ToString();
            var section = Referenced(new[] { ("x", value, false) }, new[] { (long)count - 1 });
            Assert.That(SectionWire(section), Is.EqualTo(new byte[] { (byte)(count % 4 + 1), 0, 128 }));
            Assert.That(SectionReferences(section), Is.EqualTo(new[] { (long)count - 1 }));
            Assert.That(Fields(decoder.Section(Convert.ToHexString(SectionWire(section)))), Is.EqualTo(new[] { "x: " + value }));
        }

        [Test]
        public void RelativeReferencesPreserveOrderAndDeduplicateOwnership()
        {
            var encoder = new EncoderTable(68);
            var decoder = new Table(68);
            decoder.Feed(Convert.ToHexString(EncoderTable.Wire(encoder.Insert("x", "a"))));
            decoder.Feed(Convert.ToHexString(EncoderTable.Wire(encoder.Insert("y", "b"))));
            var section = Referenced(new[] { ("x", "a", false), ("y", "b", false), ("x", "a", false) }, new long[] { 0, 1, 0 });
            Assert.That(Convert.ToHexString(SectionWire(section)), Is.EqualTo("0300818081"));
            Assert.That(SectionReferences(section), Is.EquivalentTo(new long[] { 0, 1 }));
            Assert.That(Fields(decoder.Section(Convert.ToHexString(SectionWire(section)))), Is.EqualTo(new[] { "x: a", "y: b", "x: a" }));
        }

        [TestCase("authorization", false)]
        [TestCase("proxy-authorization", false)]
        [TestCase("cookie", false)]
        [TestCase("set-cookie", false)]
        [TestCase("x-secret", true)]
        public void SuppliedDynamicIndexCannotOverrideNeverIndexPolicy(string name, bool never)
        {
            var section = Referenced(new[] { (name, "secret", never) }, new long[] { 0 }, 0);
            Assert.That(SectionReferences(section), Is.Empty);
            var fields = Decode(new Table(0).Section(Convert.ToHexString(SectionWire(section)))) ?? throw new AssertionException("Blocked literal.");
            Assert.That(Value(fields.Single(), "NeverIndexed"), Is.True);
        }

        [TestCase(-2L)]
        [TestCase(4611686018427387903L)]
        public void ReferencedEncoderRejectsInvalidAbsoluteIndices(long index)
        {
            Assert.That(() => Referenced(new[] { ("x", "a", false) }, new[] { index }), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [TestCase(0)]
        [TestCase(31)]
        public void ReferenceRequiresCapacityForAtLeastOneEntry(int capacity)
        {
            Assert.That(() => Referenced(new[] { ("x", "a", false) }, new long[] { 0 }, capacity), Throws.ArgumentException);
        }

        [Test]
        public void ReferencedEncodingHonorsEncodedAndDecodedLimits()
        {
            var fields = new[] { ("x", "a", false) };
            Assert.That(SectionWire(Referenced(fields, new long[] { 0 }, encoded: 3, decoded: 34)), Has.Length.EqualTo(3));
            Assert.That(() => Referenced(fields, new long[] { 0 }, encoded: 2), Throws.InstanceOf<InvalidDataException>());
            Assert.That(() => Referenced(fields, new long[] { 0 }, decoded: 33), Throws.InstanceOf<InvalidDataException>());
            var statics = Referenced(new[] { (":status", "200", false) }, new long[] { 500 }, 0);
            Assert.That(Convert.ToHexString(SectionWire(statics)), Is.EqualTo("0000D9"));
            Assert.That(SectionReferences(statics), Is.Empty);
        }

        [TestCase(63L, 4096L, "4100BF0080")]
        [TestCase(4611686018427387902L, 4611686018427387903L, "2000BFBFFFFFFFFFFFFFFF3F80")]
        public void RelativeReferencesRetainFullCounterWidth(long latest, long capacity, string expected)
        {
            var section = Referenced(new[] { ("x", "a", false), ("y", "b", false) }, new long[] { 0, latest }, capacity);
            Assert.That(Convert.ToHexString(SectionWire(section)), Is.EqualTo(expected));
            Assert.That(SectionReferences(section), Is.EquivalentTo(new long[] { 0, latest }));
        }

        [Test]
        public void ReferencedEncodingStillValidatesFieldOctets()
        {
            Assert.That(() => Referenced(new[] { ("x", "\u0100", false) }, new long[] { 0 }), Throws.ArgumentException);
        }
    }
}
