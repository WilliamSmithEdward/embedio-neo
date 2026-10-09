using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class QpackEncoderFeedbackTest
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void PeerLimitCountsStreamsRatherThanFieldSections(int limit)
        {
            var state = new Feedback();
            state.Insert(); state.Insert();
            for (var stream = 0; stream < limit; ++stream)
            {
                Assert.That(state.LimitedSection(stream * 4, limit, 0), Is.True);
                Assert.That(state.LimitedSection(stream * 4, limit, 1), Is.True);
            }
            Assert.That(state.Blocked, Is.EqualTo(limit));
            Assert.That(state.LimitedSection(16, limit, 1), Is.False);
            Assert.That(state.Pending, Is.EqualTo(limit * 2));
            Assert.That(state.LimitedSection(16, limit), Is.True);
            state.Feed("02");
            Assert.That(state.Blocked, Is.Zero);
            Assert.That(state.LimitedSection(16, limit, 1), Is.True);
            Assert.That(state.Referenced(1), Is.True);
        }

        [Test]
        public void InsertProgressUnblocksWithoutReleasingReferences()
        {
            var state = new Feedback();
            state.Insert(); state.Insert(); state.Insert();
            Assert.That(state.LimitedSection(0, 2, 0), Is.True);
            Assert.That(state.LimitedSection(4, 2, 2), Is.True);
            state.Feed("01");
            Assert.That(state.Blocked, Is.EqualTo(1));
            Assert.That(state.Pending, Is.EqualTo(2));
            Assert.That(state.Referenced(0), Is.True);
            Assert.That(state.LimitedSection(8, 2, 1), Is.True);
            Assert.That(state.LimitedSection(12, 2, 2), Is.False);
            state.Feed("01");
            Assert.That(state.Blocked, Is.EqualTo(1));
            Assert.That(state.LimitedSection(12, 2, 2), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SectionAcknowledgmentAdvancesOtherStreamsAndPreservesLaterSections(bool newestFirst)
        {
            var state = new Feedback();
            state.Insert(); state.Insert(); state.Insert();
            state.LimitedSection(0, 2, newestFirst ? 2 : 0);
            state.LimitedSection(0, 2, newestFirst ? 0 : 2);
            state.LimitedSection(4, 2, 1);
            state.Feed("80");
            Assert.That(state.Known, Is.EqualTo(newestFirst ? 3 : 1));
            Assert.That(state.Blocked, Is.EqualTo(newestFirst ? 0 : 2));
            Assert.That(state.Pending, Is.EqualTo(2));
            state.Feed("84");
            Assert.That(state.Blocked, Is.EqualTo(newestFirst ? 0 : 1));
            state.Feed("80");
            Assert.That(state.Blocked, Is.Zero);
            Assert.That(state.Pending, Is.Zero);
            Assert.That(state.Known, Is.EqualTo(3));
        }

        [Test]
        public void CancellationFreesOneSlotWithoutAcknowledgingOtherStreams()
        {
            var state = new Feedback();
            state.Insert(); state.Insert();
            state.LimitedSection(0, 2, 0); state.LimitedSection(0, 2, 1);
            state.LimitedSection(4, 2, 1);
            state.Feed("4040");
            Assert.That(state.Blocked, Is.EqualTo(1));
            Assert.That(state.Known, Is.Zero);
            Assert.That(state.Pending, Is.EqualTo(1));
            Assert.That(state.LimitedSection(8, 2, 0), Is.True);
            Assert.That(state.LimitedSection(12, 2, 0), Is.False);
        }

        [Test]
        public void RejectedAdmissionNeitherPinsEntriesNorConsumesBudget()
        {
            var state = new Feedback(2, 2);
            state.Insert(); state.Insert();
            Assert.That(state.LimitedSection(0, 1, 0), Is.True);
            Assert.That(state.LimitedSection(4, 1, 1), Is.False);
            Assert.That(state.Referenced(1), Is.False);
            Assert.That(state.Pending, Is.EqualTo(1));
            Assert.That(state.LimitedSection(0, 1, 1), Is.True);
            Assert.That(state.LimitedSection(0, 1, 1), Is.False);
            Assert.That(state.Blocked, Is.EqualTo(1));
            Assert.That(() => state.Feed("03"), Throws.InstanceOf<System.IO.IOException>());
            Assert.That(state.Blocked, Is.Zero);
            Assert.That(state.Pending, Is.Zero);
            Assert.That(() => state.LimitedSection(8, 1, 0), Throws.InstanceOf<System.IO.IOException>());
        }

        [TestCase(-1L)]
        [TestCase(4611686018427387904L)]
        public void InvalidPeerBlockedLimitCannotChangeOwnership(long limit)
        {
            var state = new Feedback(); state.Insert();
            Assert.That(() => state.LimitedSection(0, limit, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(state.Blocked, Is.Zero);
            Assert.That(state.Pending, Is.Zero);
        }

        [Test]
        public async Task ConcurrentAdmissionsCannotOversubscribePeerLimit()
        {
            var state = new Feedback(128, 128); state.Insert();
            var results = await Task.WhenAll(Enumerable.Range(0, 64)
                .Select(stream => Task.Run(() => state.LimitedSection(stream * 4, 4, 0))));
            Assert.That(results.Count(accepted => accepted), Is.EqualTo(4));
            Assert.That(state.Blocked, Is.EqualTo(4));
            Assert.That(state.Pending, Is.EqualTo(4));
            state.Feed("01");
            Assert.That(state.Blocked, Is.Zero);
            Assert.That(state.Pending, Is.EqualTo(4));
        }

        [TestCase(181)]
        [TestCase(182)]
        [TestCase(9204)]
        public void RandomFeedbackSequencesMatchIndependentPendingSectionModel(int seed)
        {
            var state = new Feedback(4096, 8192);
            for (var i = 0; i < 32; ++i) state.Insert();
            var random = new Random(seed);
            var pending = new Dictionary<int, Queue<int>>();
            var known = 0;
            var inserted = 32;
            for (var step = 0; step < 2000; ++step)
            {
                state.Insert(); ++inserted;
                var stream = random.Next(16) * 4;
                var operation = random.Next(4);
                if (operation == 0)
                {
                    var index = random.Next(inserted - 32, inserted);
                    var blocked = pending.Count(item => item.Value.Max() >= known);
                    var alreadyBlocked = pending.TryGetValue(stream, out var sections) && sections.Max() >= known;
                    var accepted = index < known || alreadyBlocked || blocked < 4;
                    Assert.That(state.LimitedSection(stream, 4, index), Is.EqualTo(accepted), $"seed={seed}, step={step}");
                    if (accepted)
                    {
                        if (sections == null) pending.Add(stream, sections = new Queue<int>());
                        sections.Enqueue(index);
                    }
                }
                else if (operation == 1 && pending.TryGetValue(stream, out var sections))
                {
                    known = Math.Max(known, sections.Dequeue() + 1);
                    if (sections.Count == 0) pending.Remove(stream);
                    state.Feed(Convert.ToHexString(new[] { (byte)(128 + stream) }), 1);
                }
                else if (operation == 2)
                {
                    pending.Remove(stream);
                    state.Feed(Convert.ToHexString(new[] { (byte)(64 + stream) }), 1);
                }
                else if (known < inserted)
                {
                    var increment = random.Next(1, Math.Min(63, inserted - known) + 1);
                    known += increment;
                    state.Feed(Convert.ToHexString(new[] { (byte)increment }), 1);
                }
                Assert.That(state.Known, Is.EqualTo(known));
                Assert.That(state.Blocked, Is.EqualTo(pending.Count(item => item.Value.Max() >= known)));
                Assert.That(state.Pending, Is.EqualTo(pending.Sum(item => item.Value.Count)));
                for (var index = inserted - 32; index < inserted; ++index)
                    Assert.That(state.Referenced(index), Is.EqualTo(pending.Any(item => item.Value.Contains(index))));
            }
        }
    }
}
