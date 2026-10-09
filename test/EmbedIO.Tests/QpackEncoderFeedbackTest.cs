using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class QpackEncoderFeedbackTest
    {
        private sealed class Feedback(int sections = 8, int references = 16)
        {
            private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            private readonly object _value = Activator.CreateInstance(
                typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoderFeedback", true)
                    ?? throw new AssertionException("Missing feedback state."),
                Hidden, null, new object[] { sections, references }, null) ?? throw new AssertionException("Missing feedback constructor.");
            private object? Call(string method, params object[] values)
            {
                try { return (_value.GetType().GetMethod(method, Hidden) ?? throw new AssertionException("Missing method.")).Invoke(_value, values); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
            }
            internal long Insert() => Convert.ToInt64(Call("RegisterInsert"));
            internal bool Section(long stream, params long[] entries) => Convert.ToBoolean(Call("TryRegisterSection", stream, entries, 8L));
            internal bool LimitedSection(long stream, long limit, params long[] entries) => Convert.ToBoolean(Call("TryRegisterSection", stream, entries, limit));
            internal int Blocked => Convert.ToInt32(_value.GetType().GetProperty("PotentiallyBlockedStreams", Hidden)?.GetValue(_value));
            internal bool Referenced(long index) => Convert.ToBoolean(Call("IsReferenced", index));
            internal long Known => Convert.ToInt64(_value.GetType().GetProperty("KnownReceivedCount", Hidden)?.GetValue(_value));
            internal int Pending => Convert.ToInt32(_value.GetType().GetProperty("PendingSections", Hidden)?.GetValue(_value));
            internal void Feed(string hex, int fragment = 1024)
            {
                var bytes = Convert.FromHexString(hex);
                for (var offset = 0; offset < bytes.Length; offset += fragment)
                    Call("Feed", bytes, offset, Math.Min(fragment, bytes.Length - offset));
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(1024)]
        public void SectionAcksReleaseInOrderAndAdvanceKnownCount(int fragment)
        {
            var state = new Feedback();
            Assert.That(state.Insert(), Is.Zero);
            Assert.That(state.Insert(), Is.EqualTo(1));
            Assert.That(state.Section(128, 0), Is.True);
            Assert.That(state.Section(128, 1), Is.True);
            state.Feed("ff01", fragment);
            Assert.That(state.Known, Is.EqualTo(1));
            Assert.That(state.Pending, Is.EqualTo(1));
            Assert.That(state.Referenced(0), Is.False);
            Assert.That(state.Referenced(1), Is.True);
            state.Feed("01ff01", fragment);
            Assert.That(state.Known, Is.EqualTo(2));
            Assert.That(state.Pending, Is.Zero);
            Assert.That(state.Referenced(1), Is.False);
            Assert.That(() => state.Feed("ff01"), Throws.InstanceOf<IOException>());
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(1024)]
        public void CancellationReleasesOnlyThatStreamsSectionsWithoutAcknowledgingInserts(int fragment)
        {
            var state = new Feedback();
            state.Insert(); state.Insert();
            state.Section(128, 0); state.Section(128, 1); state.Section(4, 0);
            state.Feed("7f41", fragment);
            Assert.That(state.Known, Is.Zero);
            Assert.That(state.Pending, Is.EqualTo(1));
            Assert.That(state.Referenced(0), Is.True);
            Assert.That(state.Referenced(1), Is.False);
            state.Feed("7f4184", fragment);
            Assert.That(state.Pending, Is.Zero);
            Assert.That(state.Known, Is.EqualTo(1));
            Assert.That(state.Referenced(0), Is.False);
        }

        [TestCase("00")]
        [TestCase("01")]
        [TestCase("80")]
        [TestCase("ff01")]
        [TestCase("7fffffffffffffffffff7f")]
        [TestCase("7f80808080808080808080")]
        public void InvalidPeerInstructionsFailPermanently(string hex)
        {
            var state = new Feedback();
            var error = Assert.Catch<IOException>(() => state.Feed(hex, 1));
            Assert.That(error?.GetType().GetProperty("ErrorCode")?.GetValue(error), Is.EqualTo(0x202L));
            Assert.That(() => state.Feed("40"), Throws.InstanceOf<IOException>());
            Assert.That(() => state.Insert(), Throws.InstanceOf<IOException>());
        }

        [Test]
        public void AdmissionLimitsAreTransactionalAndAcknowledgmentReleasesBudget()
        {
            var state = new Feedback(2, 2);
            state.Insert(); state.Insert();
            Assert.That(state.Section(0, 0), Is.True);
            Assert.That(state.Section(4, 0, 1), Is.False);
            Assert.That(state.Pending, Is.EqualTo(1));
            Assert.That(state.Referenced(1), Is.False);
            Assert.That(state.Section(4, 1), Is.True);
            Assert.That(state.Section(8, 0), Is.False);
            Assert.That(state.Section(12), Is.True);
            state.Feed("80");
            Assert.That(state.Section(8, 0), Is.True);
        }

        [Test]
        public void CallerCannotMutateTrackedReferencesAndFailureClearsOwnership()
        {
            var state = new Feedback();
            state.Insert(); state.Insert();
            var references = new long[] { 0, 0 };
            Assert.That(state.Section(4, references), Is.True);
            references[0] = 1;
            Assert.That(state.Referenced(0), Is.True);
            Assert.That(state.Referenced(1), Is.False);
            Assert.That(() => state.Feed("03"), Throws.InstanceOf<IOException>());
            Assert.That(state.Pending, Is.Zero);
            Assert.That(state.Referenced(0), Is.False);
        }

        [Test]
        public void FullWidthCancellationAndNonminimalIntegerAreAccepted()
        {
            var state = new Feedback();
            state.Feed("7fc0ffffffffffffff3f", 1);
            state.Feed("7f8000", 1);
            Assert.That(state.Pending, Is.Zero);
            Assert.That(state.Known, Is.Zero);
        }
    }
}
