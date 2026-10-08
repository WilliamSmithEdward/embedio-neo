using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3QpackTableTest
    {
        private static object[]? Decode(object section)
        {
            try
            {
                var decoded = (IEnumerable?)(section.GetType().GetMethod("TryDecode", Hidden)
                    ?? throw new AssertionException("Missing decoder.")).Invoke(section, null);
                return decoded?.Cast<object>().ToArray();
            }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static string[] Fields(object section) => (Decode(section) ?? throw new AssertionException("Unexpected blocked section."))
            .Select(field => Value(field, "Name") + ": " + Value(field, "Value")).ToArray();

        [Test]
        public void PublishedStaticLiteralSectionDecodesWithoutDynamicCapacity()
        {
            var table = new Table(0);
            Assert.That(Fields(table.Section("0000510b2f696e6465782e68746d6c")), Is.EqualTo(new[] { ":path: /index.html" }));
            Assert.That(Fields(table.Section("0000")), Is.Empty);
        }

        [TestCase("20")]
        [TestCase("00")]
        [TestCase("c000")]
        public void ZeroAdvertisedCapacityForbidsEncoderInstructions(string instruction)
        {
            var table = new Table(0);
            Error(() => table.Feed(instruction), 0x201);
        }

        [Test]
        public void PublishedPostBaseSectionWaitsForEncoderInserts()
        {
            var table = new Table();
            var section = table.Section("03811011");
            Assert.That(Decode(section), Is.Null);
            table.Feed("3fbd01c00f7777772e6578616d706c652e636f6d");
            Assert.That(Decode(section), Is.Null);
            table.Feed("c10c2f73616d706c652f70617468");
            Assert.That(Fields(section), Is.EqualTo(new[] { ":authority: www.example.com", ":path: /sample/path" }));
        }

        [TestCase("020080", "a: b", false)]
        [TestCase("0200400163", "a: c", false)]
        [TestCase("0200600163", "a: c", true)]
        [TestCase("028010", "a: b", false)]
        [TestCase("0280000163", "a: c", false)]
        [TestCase("0280080163", "a: c", true)]
        [TestCase("000031610163", "a: c", true)]
        [TestCase("000021610163", "a: c", false)]
        [TestCase("0000710163", ":path: c", true)]
        public void EveryRepresentationPreservesFieldsAndNeverIndexed(string wire, string expected, bool neverIndexed)
        {
            var table = new Table();
            table.Feed("3fbd0141610162");
            var section = table.Section(wire);
            Assert.That(Fields(section), Is.EqualTo(new[] { expected }));
            var fields = Decode(section) ?? throw new AssertionException("Blocked.");
            Assert.That(Value(fields[0], "NeverIndexed"), Is.EqualTo(neverIndexed));
        }

        [Test]
        public void LiteralNameAndValueHuffmanDecode()
        {
            var table = new Table(0);
            Assert.That(Fields(table.Section("00002f0125a849e95ba97d7f8925a849e95bb8e8b4bf")),
                Is.EqualTo(new[] { "custom-key: custom-value" }));
        }

        [Test]
        public void StaticIndexedFieldsPreserveOrderAndDuplicates()
        {
            var table = new Table(0);
            Assert.That(Fields(table.Section("0000d1c1d1")), Is.EqualTo(new[] { ":method: GET", ":path: /", ":method: GET" }));
            for (var index = 0; index < 99; index++)
            {
                var wire = index < 63 ? new byte[] { 0, 0, (byte)(192 + index) } : new byte[] { 0, 0, 255, (byte)(index - 63) };
                Assert.That(Fields(table.Section(Convert.ToHexString(wire))), Has.Length.EqualTo(1));
            }
        }

        [TestCase("0000ff24")]
        [TestCase("00005f5400")]
        [TestCase("000080")]
        [TestCase("000010")]
        [TestCase("02008081")]
        [TestCase("020010")]
        [TestCase("0200d1")]
        [TestCase("00005103")]
        [TestCase("00002884ffffffff")]
        [TestCase("00005f80808080808080808000")]
        [TestCase("00005f")]
        public void MalformedFieldSectionsFailWithDecompressionError(string wire)
        {
            var table = new Table();
            table.Feed("3fbd0141610162");
            Error(() => Decode(table.Section(wire)), 0x200);
            Assert.That(Fields(table.Section("0000d1")), Is.EqualTo(new[] { ":method: GET" }));
        }

        [Test]
        public void EvictedReferenceFailsEvenWhenRequiredCountIsAvailable()
        {
            var table = new Table(34);
            table.Feed("3f0341610162");
            var section = table.Section("020080");
            table.Feed("41630164");
            Error(() => Decode(section), 0x200);
        }

        [Test]
        public void BlockedPrefixIsNotReinterpretedAfterCounterWrap()
        {
            var table = new Table(68);
            var section = table.Section("028010");
            Assert.That(Decode(section), Is.Null);
            table.Feed("3f254161016200000000");
            Assert.That(table.Count, Is.EqualTo(5));
            Assert.That(Value(section, "RequiredInsertCount"), Is.EqualTo(1L));
            Error(() => Decode(section), 0x200);
        }

        [Test]
        public void EncodedAndDecodedLimitsIncludeRepeatedIndexedFields()
        {
            var table = new Table(0);
            Error(() => table.Section("0000d1", 2), 0x107);
            Assert.That(Fields(table.Section("0000d1", 3, 42)), Has.Length.EqualTo(1));
            Error(() => Decode(table.Section("0000d1", 3, 41)), 0x107);
            Error(() => Decode(table.Section("0000d1d1", 4, 83)), 0x107);
            Error(() => Decode(table.Section("000021610162", 6, 33)), 0x107);
        }
    }
}
