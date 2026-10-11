using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3QpackTableTest
    {
        private sealed class EncoderTable
        {
            private readonly object _table;
            private readonly object _feedback;
            internal EncoderTable(int capacity)
            {
                var assembly = typeof(WebServer).Assembly;
                _feedback = Activator.CreateInstance(assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoderFeedback", true)
                    ?? throw new AssertionException("Missing feedback."), Hidden, null, new object[] { 16, 64 }, null)
                    ?? throw new AssertionException("Missing feedback constructor.");
                _table = Activator.CreateInstance(assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoderTable", true)
                    ?? throw new AssertionException("Missing table."), Hidden, null, new object[] { capacity, _feedback }, null)
                    ?? throw new AssertionException("Missing table constructor.");
            }
            private static object? Call(object target, string method, params object[] args)
            {
                try { return (target.GetType().GetMethod(method, Hidden) ?? throw new AssertionException("Missing method.")).Invoke(target, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
            }
            internal object? Insert(string name, string value, int limit = 65536, bool never = false)
            {
                var field = Activator.CreateInstance(typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true)
                    ?? throw new AssertionException("Missing field."), Hidden, null, new object[] { name, value, never }, null)
                    ?? throw new AssertionException("Missing field constructor.");
                return Call(_table, "TryInsert", field, limit);
            }
            internal long Find(string name, string value) => Convert.ToInt64(Call(_table, "Find", name, value));
            internal int Bytes => Convert.ToInt32(_table.GetType().GetProperty("StoredBytes", Hidden)?.GetValue(_table));
            internal void Feedback(string hex) { var bytes = Convert.FromHexString(hex); Call(_feedback, "Feed", bytes, 0, bytes.Length); }
            internal void Section(long stream, params long[] indices) => Assert.That(Call(_feedback, "TryRegisterSection", stream, indices, 16L), Is.True);
            internal static byte[] Wire(object? insertion) => (byte[])Value(insertion ?? throw new AssertionException("Insertion refused."), "Instructions");
            internal static long Index(object? insertion) => (long)Value(insertion ?? throw new AssertionException("Insertion refused."), "Index");
        }

        [TestCase("x", "a")]
        [TestCase(":authority", "www.example.com")]
        [TestCase("custom-key", "custom-value")]
        [TestCase("x-octets", "caféÿ")]
        public void DynamicInsertionInstructionsRoundTripThroughDecoder(string name, string value)
        {
            var encoder = new EncoderTable(220);
            var decoder = new Table(220);
            var insertion = encoder.Insert(name, value);
            Assert.That(EncoderTable.Index(insertion), Is.Zero);
            var wire = EncoderTable.Wire(insertion);
            foreach (var octet in wire) decoder.Feed(new[] { octet }, 0, 1);
            Assert.That(decoder.Entry(0), Is.EqualTo(name + ": " + value));
            Assert.That(decoder.Bytes, Is.EqualTo(encoder.Bytes));
            Assert.That(decoder.Capacity, Is.EqualTo(220));
        }

        [TestCase("authorization", false)]
        [TestCase("proxy-authorization", false)]
        [TestCase("Cookie", false)]
        [TestCase("set-cookie", false)]
        [TestCase("x-secret", true)]
        public void DynamicTableNeverStoresSensitiveFields(string name, bool never)
        {
            var encoder = new EncoderTable(220);
            Assert.That(encoder.Insert(name, "secret", never: never), Is.Null);
            Assert.That(encoder.Bytes, Is.Zero);
            Assert.That(EncoderTable.Index(encoder.Insert("x", "a")), Is.Zero);
        }

        [TestCase(0, false)]
        [TestCase(33, false)]
        [TestCase(34, true)]
        public void EntrySizeIncludesNameValueAndThirtyTwoBytes(int capacity, bool fits)
        {
            var encoder = new EncoderTable(capacity);
            Assert.That(encoder.Insert("x", "a") != null, Is.EqualTo(fits));
            Assert.That(encoder.Bytes, Is.EqualTo(fits ? 34 : 0));
        }

        [Test]
        public void EvictionRequiresBothAcknowledgmentAndReferenceRelease()
        {
            var encoder = new EncoderTable(34);
            var decoder = new Table(34);
            decoder.Feed(Convert.ToHexString(EncoderTable.Wire(encoder.Insert("x", "a"))));
            Assert.That(encoder.Insert("y", "b"), Is.Null);
            encoder.Section(0, 0);
            encoder.Feedback("01");
            Assert.That(encoder.Insert("y", "b"), Is.Null);
            encoder.Feedback("80");
            var next = encoder.Insert("y", "b");
            Assert.That(EncoderTable.Index(next), Is.EqualTo(1));
            Assert.That(Convert.ToHexString(EncoderTable.Wire(next)), Is.EqualTo("41790162"));
            decoder.Feed(Convert.ToHexString(EncoderTable.Wire(next)));
            Assert.That(decoder.Entry(1), Is.EqualTo("y: b"));
            Assert.That(encoder.Find("x", "a"), Is.EqualTo(-1));
            Assert.That(encoder.Find("y", "b"), Is.EqualTo(1));
        }

        [Test]
        public void RefusedInsertionDoesNotPartiallyEvictAnEligiblePrefix()
        {
            var encoder = new EncoderTable(68);
            encoder.Insert("x", "a"); encoder.Insert("y", "b");
            encoder.Feedback("02");
            encoder.Section(4, 1);
            Assert.That(encoder.Insert("z", new string('c', 10)), Is.Null);
            Assert.That(encoder.Bytes, Is.EqualTo(68));
            Assert.That(encoder.Find("x", "a"), Is.Zero);
            encoder.Feedback("44");
            Assert.That(EncoderTable.Index(encoder.Insert("z", new string('c', 10))), Is.EqualTo(2));
            Assert.That(encoder.Bytes, Is.EqualTo(43));
        }

        [Test]
        public void InstructionBudgetFailurePreservesCapacityAndInsertionCount()
        {
            var encoder = new EncoderTable(68);
            Assert.That(encoder.Insert("x", "a", 5), Is.Null);
            Assert.That(encoder.Bytes, Is.Zero);
            var first = encoder.Insert("x", "a", 6);
            Assert.That(EncoderTable.Index(first), Is.Zero);
            Assert.That(Convert.ToHexString(EncoderTable.Wire(first)), Is.EqualTo("3F2541780161"));
            encoder.Feedback("01");
            Assert.That(encoder.Insert("y", "b", 3), Is.Null);
            Assert.That(EncoderTable.Index(encoder.Insert("y", "b", 4)), Is.EqualTo(1));
        }

        [Test]
        public void ExactDuplicateDoesNotConsumeTableCapacityOrAbsoluteIndex()
        {
            var encoder = new EncoderTable(68);
            encoder.Insert("x", "a");
            Assert.That(encoder.Insert("x", "a"), Is.Null);
            Assert.That(encoder.Bytes, Is.EqualTo(34));
            Assert.That(EncoderTable.Index(encoder.Insert("y", "b")), Is.EqualTo(1));
        }

        [Test]
        public void InvalidOctetsDoNotMutateEncoderState()
        {
            var encoder = new EncoderTable(220);
            Assert.That(() => encoder.Insert("x", "\u0100"), Throws.ArgumentException);
            Assert.That(encoder.Bytes, Is.Zero);
            Assert.That(EncoderTable.Index(encoder.Insert("x", "a")), Is.Zero);
        }
    }
}
