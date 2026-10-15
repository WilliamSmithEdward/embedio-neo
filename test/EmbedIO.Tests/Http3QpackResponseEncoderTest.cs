using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3QpackTableTest
    {
        private sealed class ResponseEncoder
        {
            private readonly object _owner;
            private readonly object _feedback;
            private readonly object _peer;
            internal ResponseEncoder(int capacity = 34, int budget = 65536, bool enabled = true)
            {
                var assembly = typeof(WebServer).Assembly;
                object Create(string name, params object[] args) => Activator.CreateInstance(
                    assembly.GetType("EmbedIO.Net.Internal.Http3." + name, true) ?? throw new AssertionException("Missing type."),
                    Hidden, null, args, null) ?? throw new AssertionException("Missing constructor.");
                _feedback = Create("QpackEncoderFeedback", 16, 64);
                _owner = Create("QpackResponseEncoder", _feedback, capacity, budget);
                var settings = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3PeerSettings", true) ?? throw new AssertionException("Missing settings.");
                _peer = settings.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null,
                    new object[] { enabled ? Convert.FromHexString("0150000700") : Array.Empty<byte>(), 1024 }) ?? throw new AssertionException("Missing settings parser.");
            }
            private static object? Call(object target, string name, params object[] args)
            {
                try { return target.GetType().GetMethod(name, Hidden)?.Invoke(target, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
            }
            internal byte[] Encode(long stream, params (string Name, string Value, bool Never)[] fields)
            {
                var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new AssertionException("Missing field.");
                var array = Array.CreateInstance(type, fields.Length);
                for (var i = 0; i < fields.Length; ++i)
                    array.SetValue(Activator.CreateInstance(type, Hidden, null, new object[] { fields[i].Name, fields[i].Value, fields[i].Never }, null), i);
                return (byte[])(Call(_owner, "Encode", stream, array, _peer, 65536, 65536) ?? throw new AssertionException("Missing encoded response."));
            }
            internal byte[]? Dequeue() => (byte[]?)Call(_owner, "DequeueInstructions");
            internal int Pending => Convert.ToInt32(_owner.GetType().GetProperty("PendingBytes", Hidden)?.GetValue(_owner));
            internal void Feedback(string hex) { var bytes = Convert.FromHexString(hex); Call(_feedback, "Feed", bytes, 0, bytes.Length); }
        }

        [Test]
        public void ResponsesUseDynamicEntriesOnlyAfterDecoderAcknowledgesInsertion()
        {
            var encoder = new ResponseEncoder();
            var decoder = new Table(4096);
            var cold = encoder.Encode(0, ("x", "a", false));
            Assert.That(cold[0], Is.Zero);
            var instructions = encoder.Dequeue() ?? throw new AssertionException("Missing insertion.");
            decoder.Feed(instructions, 0, instructions.Length);
            Assert.That(Fields(decoder.Section(Convert.ToHexString(cold))), Is.EqualTo(new[] { "x: a" }));
            Assert.That(encoder.Encode(4, ("x", "a", false)), Is.EqualTo(cold));
            Assert.That(encoder.Dequeue(), Is.Null);
            encoder.Feedback("01");
            var warm = encoder.Encode(8, ("x", "a", false));
            Assert.That(Convert.ToHexString(warm), Is.EqualTo("020080"));
            Assert.That(Fields(decoder.Section(Convert.ToHexString(warm))), Is.EqualTo(new[] { "x: a" }));
            encoder.Feedback("88");
        }

        [Test]
        public void ZeroPeerCapacityLeavesResponseStatelessWithoutInstructions()
        {
            var encoder = new ResponseEncoder(enabled: false);
            Assert.That(encoder.Encode(0, ("x", "a", false))[0], Is.Zero);
            Assert.That(encoder.Pending, Is.Zero);
            Assert.That(encoder.Dequeue(), Is.Null);
        }

        [TestCase(0)]
        [TestCase(5)]
        [TestCase(6)]
        public void EncoderInstructionBudgetIncludesInitialCapacity(int budget)
        {
            var encoder = new ResponseEncoder(budget: budget);
            encoder.Encode(0, ("x", "a", false));
            Assert.That(encoder.Pending, Is.EqualTo(budget == 6 ? 6 : 0));
            Assert.That(encoder.Pending, Is.LessThanOrEqualTo(budget));
            encoder.Dequeue();
            Assert.That(encoder.Pending, Is.Zero);
        }

        [Test]
        public void ResponseReferencesArePinnedBeforeLaterFieldsCanEvictThem()
        {
            var encoder = new ResponseEncoder();
            encoder.Encode(0, ("x", "a", false)); encoder.Dequeue(); encoder.Feedback("01");
            var wire = encoder.Encode(4, ("x", "a", false), ("y", "b", false));
            Assert.That(wire[0], Is.EqualTo(2));
            Assert.That(encoder.Dequeue(), Is.Null);
            // A second attempt passes admission but must still respect pins.
            Assert.That(encoder.Encode(8, ("x", "a", false), ("y", "b", false))[0], Is.EqualTo(2));
            Assert.That(encoder.Dequeue(), Is.Null);
            encoder.Feedback("8488");
            encoder.Encode(12, ("y", "b", false));
            Assert.That(Convert.ToHexString(encoder.Dequeue() ?? Array.Empty<byte>()), Is.EqualTo("41790162"));
        }

        [Test]
        public void TablePressureRequiresARepeatedCandidateBeforeEviction()
        {
            var encoder = new ResponseEncoder();
            encoder.Encode(0, ("x", "a", false)); encoder.Dequeue(); encoder.Feedback("01");
            Assert.That(encoder.Encode(4, ("y", "b", false))[0], Is.Zero);
            Assert.That(encoder.Dequeue(), Is.Null);
            Assert.That(encoder.Encode(8, ("y", "b", false))[0], Is.Zero);
            Assert.That(Convert.ToHexString(encoder.Dequeue() ?? Array.Empty<byte>()), Is.EqualTo("41790162"));
            encoder.Feedback("01");
            Assert.That(Convert.ToHexString(encoder.Encode(12, ("y", "b", false))), Is.EqualTo("030080"));
            encoder.Feedback("8C");
        }

        [Test]
        public void OneOffResponsesPreserveAnAcknowledgedFullTableEntry()
        {
            var encoder = new ResponseEncoder(capacity: 133);
            var retained = new string('a', 100);
            encoder.Encode(0, ("x", retained, false)); encoder.Dequeue(); encoder.Feedback("01");
            for (var i = 0; i < 200; ++i)
            {
                var value = i.ToString("D100", System.Globalization.CultureInfo.InvariantCulture);
                Assert.That(encoder.Encode(4L + i * 4, ("x", value, false))[0], Is.Zero);
                Assert.That(encoder.Dequeue(), Is.Null);
            }
            Assert.That(Convert.ToHexString(encoder.Encode(804, ("x", retained, false))), Is.EqualTo("020080"));
        }

        [TestCase("cookie", false)]
        [TestCase("x", true)]
        public void SensitiveResponseFieldsNeverGenerateInsertions(string name, bool never)
        {
            var encoder = new ResponseEncoder(capacity: 4096);
            Assert.That(encoder.Encode(0, (name, "secret", never))[0], Is.Zero);
            Assert.That(encoder.Pending, Is.Zero);
        }

        [Test]
        public void InvalidResponseFieldsCannotQueuePartialInsertions()
        {
            var encoder = new ResponseEncoder(capacity: 4096);
            Assert.That(() => encoder.Encode(0, ("x", "a", false), ("y", "\u0100", false)), Throws.ArgumentException);
            Assert.That(encoder.Pending, Is.Zero);
        }
    }
}
