using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3QpackTableTest
    {
        private sealed class Coordinator : IDisposable
        {
            private readonly object _instance;
            internal Coordinator(int capacity = 220, int streams = 2, long bytes = 65536, int feedback = 1024, int decoded = 65536)
            {
                var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.QpackDecoder", true) ?? throw new AssertionException("Missing coordinator.");
                _instance = Activator.CreateInstance(type, Hidden, null, new object[] { capacity, streams, 65536, decoded, bytes, feedback }, null)
                    ?? throw new AssertionException("Missing coordinator instance.");
            }
            private object? Call(string name, params object[] values)
            {
                try { return (_instance.GetType().GetMethod(name, Hidden) ?? throw new AssertionException("Missing coordinator method.")).Invoke(_instance, values); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
            }
            internal object[]? Submit(long stream, string wire) => ((Array?)Call("Submit", stream, Convert.FromHexString(wire)))?.Cast<object>().ToArray();
            internal object[] Feed(string hex)
            {
                var bytes = Convert.FromHexString(hex);
                return ((Array?)Call("FeedEncoder", bytes, 0, bytes.Length) ?? throw new AssertionException("Missing completions.")).Cast<object>().ToArray();
            }
            internal string Feedback() => Convert.ToHexString((byte[]?)Call("DrainFeedback") ?? throw new AssertionException("Missing feedback."));
            internal void Cancel(long stream) => Call("Cancel", stream);
            internal void Closed() => Call("EncoderClosed");
            internal void Abort() => Call("Abort");
            internal int Blocked => Convert.ToInt32(Value(_instance, "BlockedStreams"), System.Globalization.CultureInfo.InvariantCulture);
            internal long Bytes => Convert.ToInt64(Value(_instance, "BlockedBytes"), System.Globalization.CultureInfo.InvariantCulture);
            internal long Known => Convert.ToInt64(Value(_instance, "KnownReceivedCount"), System.Globalization.CultureInfo.InvariantCulture);
            public void Dispose() => ((IDisposable)_instance).Dispose();
        }

        [TestCase("indexed", 83)]
        [TestCase("literal", 64)]
        [TestCase("huffman", 53)]
        public void OversizedDecodedSectionDoesNotPoisonTheSharedDecoder(string kind, int limit)
        {
            using var decoder = new Coordinator(decoded: limit);
            var wire = kind switch
            {
                "indexed" => "0000d1d1",
                "literal" => "0000216120" + string.Concat(Enumerable.Repeat("61", 32)),
                _ => "00002f0125a849e95ba97d7f8925a849e95bb8e8b4bf",
            };
            Error(() => decoder.Submit(0, wire), 0x107);
            Assert.That(decoder.Submit(4, "0000d1"), Has.Length.EqualTo(1));
            Assert.That(decoder.Blocked, Is.Zero);
            Assert.That(decoder.Bytes, Is.Zero);
        }

        [Test]
        public void OversizedUnblockedSectionDoesNotCancelAHealthyBlockedSibling()
        {
            using var decoder = new Coordinator(decoded: 64);
            Assert.That(decoder.Submit(0, "02008080"), Is.Null);
            Assert.That(decoder.Submit(4, "020080"), Is.Null);
            var completed = decoder.Feed("3fbd0141610162");
            Assert.That(completed, Has.Length.EqualTo(2));
            var rejected = completed.Single(item => (long)Value(item, "StreamId") == 0);
            var error = Value(rejected, "Error");
            Assert.That(error.GetType().Name, Is.EqualTo("Http3StreamException"));
            Assert.That(Value(error, "ErrorCode"), Is.EqualTo(0x107L));
            var healthy = completed.Single(item => (long)Value(item, "StreamId") == 4);
            Assert.That((Array)Value(healthy, "Fields"), Has.Length.EqualTo(1));
            Assert.That(decoder.Blocked, Is.Zero);
            Assert.That(decoder.Bytes, Is.Zero);
            Assert.That(decoder.Feedback(), Is.EqualTo("84"));
            decoder.Cancel(0);
            Assert.That(decoder.Feedback(), Is.EqualTo("40"));
            Assert.That(decoder.Submit(8, "020080"), Has.Length.EqualTo(1));
            Assert.That(decoder.Feedback(), Is.EqualTo("88"));
        }

        [Test]
        public void UnblockingSectionAcknowledgmentReplacesRedundantInsertIncrement()
        {
            using var decoder = new Coordinator();
            Assert.That(decoder.Submit(4, "028010"), Is.Null);
            Assert.That(decoder.Blocked, Is.EqualTo(1));
            Assert.That(decoder.Bytes, Is.EqualTo(3));
            Assert.That(decoder.Feed("3fbd01416101"), Is.Empty);
            Assert.That(decoder.Feedback(), Is.Empty);
            var completed = decoder.Feed("62");
            Assert.That(completed, Has.Length.EqualTo(1));
            Assert.That(Value(completed[0], "StreamId"), Is.EqualTo(4L));
            Assert.That(decoder.Feedback(), Is.EqualTo("84"));
            Assert.That(decoder.Known, Is.EqualTo(1));
            Assert.That(decoder.Blocked, Is.Zero);
            Assert.That(decoder.Bytes, Is.Zero);
            Assert.That(decoder.Feedback(), Is.Empty);
        }

        [Test]
        public void UnreferencedInsertsAreReportedOnceAndTrailersReceiveTheirOwnAck()
        {
            using var decoder = new Coordinator();
            decoder.Feed("3fbd0141610162");
            Assert.That(decoder.Feedback(), Is.EqualTo("01"));
            Assert.That(decoder.Submit(128, "020080"), Has.Length.EqualTo(1));
            Assert.That(decoder.Submit(128, "020080"), Has.Length.EqualTo(1));
            Assert.That(decoder.Feedback(), Is.EqualTo("FF01FF01"));
            Assert.That(decoder.Feed(""), Is.Empty);
            Assert.That(decoder.Feedback(), Is.Empty);
            Assert.That(decoder.Known, Is.EqualTo(1));
        }

        [Test]
        public void DifferentRequiredCountsWakeOnlyReadyStreams()
        {
            using var decoder = new Coordinator();
            decoder.Submit(0, "038111");
            decoder.Submit(4, "028010");
            var first = decoder.Feed("3fbd0141610162");
            Assert.That(Value(first.Single(), "StreamId"), Is.EqualTo(4L));
            Assert.That(decoder.Feedback(), Is.EqualTo("84"));
            Assert.That(decoder.Blocked, Is.EqualTo(1));
            var second = decoder.Feed("41630164");
            Assert.That(Value(second.Single(), "StreamId"), Is.EqualTo(0L));
            Assert.That(decoder.Feedback(), Is.EqualTo("80"));
            Assert.That(decoder.Known, Is.EqualTo(2));
        }

        [Test]
        public void RemainingUnreferencedInsertIncrementFollowsSectionAck()
        {
            using var decoder = new Coordinator();
            decoder.Submit(0, "028010");
            Assert.That(decoder.Feed("3fbd014161016241630164"), Has.Length.EqualTo(1));
            Assert.That(decoder.Feedback(), Is.EqualTo("8001"));
            Assert.That(decoder.Known, Is.EqualTo(2));
        }

        [Test]
        public void CancellationReleasesBlockedStorageWithoutAcknowledgingInserts()
        {
            using var decoder = new Coordinator(streams: 1, bytes: 3);
            decoder.Submit(64, "028010");
            decoder.Cancel(64);
            Assert.That(decoder.Feedback(), Is.EqualTo("7F01"));
            Assert.That(decoder.Known, Is.Zero);
            Assert.That(decoder.Bytes, Is.Zero);
            Assert.That(decoder.Blocked, Is.Zero);
            decoder.Submit(4, "028010");
            Assert.That(decoder.Feed("3fbd0141610162"), Has.Length.EqualTo(1));
            Assert.That(decoder.Feedback(), Is.EqualTo("84"));
        }

        [Test]
        public void ZeroCapacityStaticSectionsAndCancellationsNeedNoFeedback()
        {
            using var decoder = new Coordinator(capacity: 0, streams: 0, bytes: 0);
            Assert.That(decoder.Submit(0, "0000D1"), Has.Length.EqualTo(1));
            decoder.Cancel(0);
            Assert.That(decoder.Feedback(), Is.Empty);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void AdvertisedBlockedStreamLimitIsEnforced(int limit)
        {
            using var decoder = new Coordinator(streams: limit);
            if (limit != 0) decoder.Submit(0, "028010");
            Error(() => decoder.Submit(4, "028010"), 0x200);
            Assert.That(decoder.Blocked, Is.Zero);
            Error(() => decoder.Feedback(), 0x200);
        }

        [Test]
        public void AggregateBlockedBytesCannotGrowBeyondLocalBudget()
        {
            using var decoder = new Coordinator(bytes: 5);
            decoder.Submit(0, "028010");
            Error(() => decoder.Submit(4, "028010"), 0x107);
            Assert.That(decoder.Bytes, Is.Zero);
        }

        [Test]
        public void FeedbackStorageIsBoundedWhenWriterDoesNotDrain()
        {
            using var decoder = new Coordinator(feedback: 1);
            decoder.Feed("3fbd0141610162");
            Error(() => decoder.Submit(0, "020080"), 0x107);
            Error(() => decoder.Feedback(), 0x107);
        }

        [Test]
        public void MalformedEncoderInstructionAbortsAllPendingSections()
        {
            using var decoder = new Coordinator();
            decoder.Submit(0, "028010");
            Error(() => decoder.Feed("00"), 0x201);
            Assert.That(decoder.Bytes, Is.Zero);
            Assert.That(decoder.Blocked, Is.Zero);
            Error(() => decoder.Submit(4, "0000"), 0x201);
            Error(decoder.Closed, 0x201);
        }

        [Test]
        public void CriticalStreamClosureAndRepeatedDisposalReleaseState()
        {
            using var decoder = new Coordinator();
            decoder.Submit(0, "028010");
            Error(decoder.Closed, 0x104);
            Assert.That(decoder.Bytes, Is.Zero);
            Error(() => decoder.Feed(""), 0x104);
            decoder.Dispose();
            decoder.Dispose();
            decoder.Abort();
        }

        [Test]
        public void CallerCannotSubmitTrailersUntilBlockedHeadersResume()
        {
            using var decoder = new Coordinator();
            decoder.Submit(0, "028010");
            Assert.Throws<InvalidOperationException>(() => decoder.Submit(0, "0000"));
            Assert.That(decoder.Feed("3fbd0141610162"), Has.Length.EqualTo(1));
            Assert.That(decoder.Submit(0, "0000D1"), Has.Length.EqualTo(1));
        }
    }
}
