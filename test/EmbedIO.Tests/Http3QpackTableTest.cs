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
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private sealed class Table
        {
            private readonly object _instance;
            internal Table(int capacity = 220)
            {
                var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.QpackDecoderTable", true)
                    ?? throw new AssertionException("Missing QPACK table.");
                _instance = Activator.CreateInstance(type, Hidden, null, new object[] { capacity }, null)
                    ?? throw new AssertionException("Missing table instance.");
            }
            private object? Call(string method, params object[] args)
            {
                try { return (_instance.GetType().GetMethod(method, Hidden) ?? throw new AssertionException("Missing table method.")).Invoke(_instance, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
            }
            internal void Feed(byte[] bytes, int offset, int count) => Call("Feed", bytes, offset, count);
            internal void Feed(string hex) { var bytes = Convert.FromHexString(hex); Feed(bytes, 0, bytes.Length); }
            internal object Section(string wire, int encoded = 65536, int decoded = 65536) =>
                Call("ReadSection", Convert.FromHexString(wire), encoded, decoded) ?? throw new AssertionException("Missing section.");
            internal void End() => Call("CompleteInput");
            internal string Entry(long index)
            {
                var field = Call("GetAbsolute", index) ?? throw new AssertionException("Missing field.");
                return Value(field, "Name") + ": " + Value(field, "Value");
            }
            internal long Count => Convert.ToInt64(Value(_instance, "InsertCount"), System.Globalization.CultureInfo.InvariantCulture);
            internal int Bytes => Convert.ToInt32(Value(_instance, "StoredBytes"), System.Globalization.CultureInfo.InvariantCulture);
            internal int Capacity => Convert.ToInt32(Value(_instance, "Capacity"), System.Globalization.CultureInfo.InvariantCulture);
        }
        private static object Value(object instance, string property) => (instance.GetType().GetProperty(property)
            ?? throw new AssertionException("Missing property.")).GetValue(instance) ?? throw new AssertionException("Missing property value.");
        private static void Error(Action action, long code)
        {
            var error = Assert.Catch<IOException>(action) ?? throw new AssertionException("Expected protocol error.");
            Assert.That(Value(error, "ErrorCode"), Is.EqualTo(code));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(7)]
        [TestCase(4096)]
        public void PublishedEncoderInstructionsSurviveArbitraryFragmentation(int chunk)
        {
            var table = new Table();
            var wire = Convert.FromHexString("3fbd01c00f7777772e6578616d706c652e636f6dc10c2f73616d706c652f70617468");
            for (var offset = 0; offset < wire.Length; offset += chunk) table.Feed(wire, offset, Math.Min(chunk, wire.Length - offset));
            table.End();
            Assert.That(table.Capacity, Is.EqualTo(220));
            Assert.That(table.Count, Is.EqualTo(2));
            Assert.That(table.Bytes, Is.EqualTo(106));
            Assert.That(table.Entry(0), Is.EqualTo(":authority: www.example.com"));
            Assert.That(table.Entry(1), Is.EqualTo(":path: /sample/path"));
        }

        [Test]
        public void PartialInstructionDoesNotMutateTable()
        {
            var table = new Table();
            table.Feed("3fbd");
            Assert.That(table.Capacity, Is.Zero);
            table.Feed("01c0036162");
            Assert.That(table.Capacity, Is.EqualTo(220));
            Assert.That(table.Count, Is.Zero);
            table.Feed("63");
            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(table.Entry(0), Is.EqualTo(":authority: abc"));
        }

        [Test]
        public void HuffmanNamesAndValuesDecodeWithExistingWireAlphabet()
        {
            var table = new Table();
            table.Feed("3fbd016825a849e95ba97d7f8925a849e95bb8e8b4bf");
            Assert.That(table.Entry(0), Is.EqualTo("custom-key: custom-value"));
        }

        [Test]
        public void DuplicateAndRelativeNameReferenceMaintainAbsoluteIndicesThroughEviction()
        {
            var table = new Table(68);
            table.Feed("3f25416101624163016400");
            Assert.That(table.Count, Is.EqualTo(3));
            Error(() => table.Entry(0), 0x200);
            Assert.That(table.Entry(1), Is.EqualTo("c: d"));
            Assert.That(table.Entry(2), Is.EqualTo("c: d"));
            table.Feed("800165");
            Assert.That(table.Entry(3), Is.EqualTo("c: e"));
            Assert.That(table.Bytes, Is.EqualTo(68));
            table.Feed("20");
            Assert.That(table.Bytes, Is.Zero);
            Error(() => table.Entry(3), 0x200);
            table.Feed("3f2541780179");
            Assert.That(table.Entry(4), Is.EqualTo("x: y"));
        }

        [TestCase("00")]
        [TestCase("8000")]
        [TestCase("ff2400")]
        [TestCase("3fbe01")]
        [TestCase("c084ffffffff")]
        [TestCase("3f0141610162")]
        [TestCase("c0ff80808080808080808000")]
        public void InvalidEncoderInstructionPoisonsTable(string wire)
        {
            var table = new Table();
            table.Feed("3fbd01");
            Error(() => table.Feed(wire), 0x201);
            Error(() => table.Feed("20"), 0x201);
            Error(() => table.Entry(0), 0x200);
        }

        [TestCase("3f")]
        [TestCase("c0")]
        [TestCase("4100")]
        [TestCase("c0036162")]
        public void IncompleteFinalInstructionIsRejected(string wire)
        {
            var table = new Table();
            table.Feed("3fbd01");
            table.Feed(wire);
            Assert.That(table.Count, Is.Zero);
            Error(table.End, 0x201);
        }

        [Test]
        public void InitialZeroCapacityDoesNotPermitInsertion()
        {
            var table = new Table();
            Error(() => table.Feed("41610162"), 0x201);
        }

        [Test]
        public void ManyInstructionsInOneInputDoNotRequireOneLargePendingBuffer()
        {
            var table = new Table(68);
            table.Feed("3f2541610162");
            table.Feed(new byte[10000], 0, 10000);
            Assert.That(table.Count, Is.EqualTo(10001));
            Assert.That(table.Bytes, Is.EqualTo(68));
            Assert.That(table.Entry(10000), Is.EqualTo("a: b"));
            Error(() => table.Entry(9998), 0x200);
        }

        [Test]
        public void AllStaticNameReferencesUseTheirZeroBasedEntries()
        {
            var table = new Table(4096);
            table.Feed("3fe11f");
            for (var i = 0; i < 99; i++)
            {
                var bytes = i < 63 ? new byte[] { (byte)(192 + i), 0 } : new byte[] { 255, (byte)(i - 63), 0 };
                table.Feed(bytes, 0, bytes.Length);
                Assert.That(table.Entry(i).EndsWith(": ", StringComparison.Ordinal), Is.True);
            }
            Assert.That(table.Entry(98), Is.EqualTo("x-frame-options: "));
        }
    }
}
