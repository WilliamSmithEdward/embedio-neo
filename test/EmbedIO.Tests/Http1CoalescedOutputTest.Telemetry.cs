using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Tests.Issues;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http1CoalescedOutputTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task HeldBodyBytesBecomeSentOnlyAfterSuccessfulTransportSubmission(bool heartbeat)
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            Assert.That(SentBodyBytes(fixture.Stream), Is.Zero);

            if (heartbeat)
            {
                var tick =
                    (int)(EngineInternals.Field(fixture.Stream, "_heldTick")
                        ?? throw new AssertionException("Missing held output tick."));
                await EngineInternals.AwaitAsync(
                    EngineInternals.Call(fixture.Stream, "SendHeartbeatAsync", unchecked(tick + 2))
                );
            }

            else
                await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "CloseWindowAsync"));

            Assert.That(SentBodyBytes(fixture.Stream), Is.EqualTo(3));
            fixture.Stream.Dispose();
            Assert.That(SentBodyBytes(fixture.Stream), Is.EqualTo(3), "Chunk framing is not response body telemetry.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedOrAbandonedHeldOutputDoesNotCountAsSentBodyBytes(bool abandon)
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            Assert.That(SentBodyBytes(fixture.Stream), Is.Zero);

            if (abandon)
                await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "AbandonAsync"));

            else
            {
                fixture.Transport.Error = new IOException("controlled telemetry submission failure");
                await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "CloseWindowAsync"));
            }

            Assert.That(SentBodyBytes(fixture.Stream), Is.Zero);
            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(SentBodyBytes(fixture.Stream), Is.Zero);
                Assert.That(fixture.Transport.Length, Is.Zero);
            }
        }

        [Test]
        public async Task ALaterHeldFailurePreservesOnlyPreviouslySubmittedBodyBytes()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("AAA"), 0, 3);
            var tick =
                (int)(EngineInternals.Field(fixture.Stream, "_heldTick")
                    ?? throw new AssertionException("Missing held output tick."));
            await EngineInternals.AwaitAsync(
                EngineInternals.Call(fixture.Stream, "SendHeartbeatAsync", unchecked(tick + 2))
            );
            fixture.Stream.Write(Encoding.ASCII.GetBytes("BBB"), 0, 3);
            Assert.That(SentBodyBytes(fixture.Stream), Is.EqualTo(3));
            fixture.Transport.Error = new IOException("controlled later held submission failure");
            await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "CloseWindowAsync"));
            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(SentBodyBytes(fixture.Stream), Is.EqualTo(3));
                Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nAAA\r\n"));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ADirectLargeBodyCountsItsPayloadWhileItsFinalChunkFramingIsHeld(bool failEnding)
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            const int Count = 65537;
            var payload = new byte[Count];
            Array.Fill(payload, (byte)'X');
            await fixture.Stream.WriteAsync(payload, 0, payload.Length);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(EngineInternals.Field(fixture.Stream, "_heldLength"), Is.EqualTo(2));
                Assert.That(SentBodyBytes(fixture.Stream), Is.EqualTo(Count));
            }

            if (failEnding) fixture.Transport.Error = new IOException("controlled final chunk framing failure");

            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(SentBodyBytes(fixture.Stream), Is.EqualTo(Count));
                Assert.That(
                    Body(fixture.Transport),
                    Is.EqualTo("10001\r\n" + new string('X', Count) + (failEnding ? "" : "\r\n0\r\n\r\n"))
                );
            }
        }

        private static object? SentBodyBytes(Stream output) => EngineInternals.Property(output, "SentBodyBytes");
    }
}
