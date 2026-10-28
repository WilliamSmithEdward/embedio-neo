using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.Issues;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public partial class Http1CoalescedOutputTest
    {
        [Test]
        public async Task CompletionWaitsForAnAlreadyAdmittedPromptnessWrite()
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Transport.BlockWrites = true;
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            await fixture.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var gate = EngineInternals.Field(fixture.Stream, "_writers")
                ?? throw new AssertionException("Missing response write gate.");
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void Complete()
            {
                try
                {
                    fixture.Stream.Dispose();
                    completed.TrySetResult();
                }

                catch (IOException error)
                {
                    completed.TrySetException(error);
                }

                catch (ObjectDisposedException error)
                {
                    completed.TrySetException(error);
                }
            }

            var closing = new Thread(Complete) { IsBackground = true };
            closing.Start();

            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(
                        () =>
                            (int)(EngineInternals.Field(gate, "_state")
                                ?? throw new AssertionException("Missing writer admission count.")),
                        Is
                            .EqualTo(2)
                            .After(5000, 10)
                    );
                    Assert.That(
                        () => (closing.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        Is.True.After(5000, 10),
                        "Synchronous completion waits while promptness I/O owns the gate."
                    );
                    Assert.That(completed.Task.IsCompleted, Is.False);
                    Assert.That(fixture.Transport.Length, Is.Zero);
                }
            }

            finally
            {
                fixture.Transport.BlockWrites = false;
                fixture.Transport.ReleaseWrites.TrySetResult();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(closing.Join(TimeSpan.FromSeconds(5)), Is.True);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    Encoding.ASCII.GetString(fixture.Transport.ToArray()),
                    Does.EndWith("\r\n\r\n3\r\nABC\r\n0\r\n\r\n")
                );
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
                Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(1));
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task InlineChunkedCompletionUsesOneTransportWrite(bool asynchronous, bool flushBetweenChunks)
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            var first = Encoding.ASCII.GetBytes("ABC");
            var second = Encoding.ASCII.GetBytes("DEF");

            if (asynchronous)
            {
                await fixture.Stream.WriteAsync(first, 0, first.Length);

                if (flushBetweenChunks) await fixture.Stream.FlushAsync();

                await fixture.Stream.WriteAsync(second, 0, second.Length);
            }

            else
            {
                fixture.Stream.Write(first, 0, first.Length);

                if (flushBetweenChunks) fixture.Stream.Flush();

                fixture.Stream.Write(second, 0, second.Length);
            }

            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fixture.Transport.AsyncWrites, Is.Zero);
                Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(1));
                Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nABC\r\n3\r\nDEF\r\n0\r\n\r\n"));
            }

            Assert.That(EngineInternals.Field(fixture.Stream, "_held"), Is.Null);
        }

        [Test]
        public async Task APoolRoundTripDoesNotDispatchNewlyHeldOutput()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            await Task.Run(static () => { });
            Assert.That(
                fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites,
                Is.Zero,
                "A held response must not queue its own immediate ThreadPool send."
            );
            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(1));
                Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nABC\r\n0\r\n\r\n"));
            }
        }

        [Test]
        public async Task AConnectionRegistrationCanMonitorSuccessiveResponsesWithoutRetainingCompletedOnes()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var registration = (IDisposable)EngineInternals.Create(
                "EmbedIO.Net.Internal.Http1OutputHeartbeat+Registration"
            );

            for (var response = 0; response < 2; response++)
            {
                using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
                EngineInternals.Call(fixture.Stream, "OpenWindow", registration);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(MonitoredCount, Is.EqualTo(1));
                    Assert.That(EngineInternals.Field(registration, "_output"), Is.Null);
                }

                fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(MonitoredCount, Is.EqualTo(1));
                    Assert.That(EngineInternals.Field(registration, "_output"), Is.SameAs(fixture.Stream));
                }

                fixture.Stream.Dispose();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(MonitoredCount, Is.EqualTo(1));
                    Assert.That(EngineInternals.Field(registration, "_output"), Is.Null);
                    Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(1));
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AHeartbeatSendsOnlyOutputHeldPastOneTickIncludingAcrossTickWrap(bool wrap)
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            var heldTick = wrap
                ? int.MaxValue
                : (int)(EngineInternals.Field(fixture.Stream, "_heldTick")
                    ?? throw new AssertionException("Missing held output tick."));
            EngineInternals.SetField(fixture.Stream, "_heldTick", heldTick);
            await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "SendHeartbeatAsync", heldTick));
            await EngineInternals.AwaitAsync(
                EngineInternals.Call(fixture.Stream, "SendHeartbeatAsync", unchecked(heldTick + 1))
            );
            Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.Zero);
            await EngineInternals.AwaitAsync(
                EngineInternals.Call(fixture.Stream, "SendHeartbeatAsync", unchecked(heldTick + 2))
            );

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
                Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nABC\r\n"));
                Assert.That(EngineInternals.Field(registration, "_output"), Is.Null);
            }

            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nABC\r\n0\r\n\r\n"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task StrictWritesInsideTheWindowReachTheTransportAndReportFailure(bool asynchronous)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: false);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Transport.Error = new IOException("controlled strict-mode transport failure");
            var payload = Encoding.ASCII.GetBytes("ABC");

            if (asynchronous)
                await Assert.ThrowsAsync<IOException>(
                    async () => await fixture.Stream.WriteAsync(payload, 0, payload.Length)
                );

            else
                Assert.Throws<IOException>(() => fixture.Stream.Write(payload, 0, payload.Length));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.EqualTo(1));
                Assert.That(EngineInternals.Field(fixture.Stream, "_held"), Is.Null);
            }

            fixture.Transport.Error = null;
            fixture.Stream.Dispose();
            Assert.That(fixture.Transport.Length, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ClosingTheWindowSendsHeldOutputAndLaterWritesThrough(bool asynchronous)
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "CloseWindowAsync"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
                Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nABC\r\n"));
            }

            if (asynchronous) await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("DEF"), 0, 3);

            else fixture.Stream.Write(Encoding.ASCII.GetBytes("DEF"), 0, 3);

            Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nABC\r\n3\r\nDEF\r\n"));
            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nABC\r\n3\r\nDEF\r\n0\r\n\r\n"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedHeldOutputIsClearedAndReleasedWithoutRetry(bool failAtWindowEnd)
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("secret held bytes"), 0, 17);
            var held =
                (byte[])(EngineInternals.Field(fixture.Stream, "_held")
                    ?? throw new AssertionException("Missing held output lease."));
            var length =
                (int)(EngineInternals.Field(fixture.Stream, "_heldLength")
                    ?? throw new AssertionException("Missing held output length."));
            fixture.Transport.Error = new IOException("controlled held output failure");

            if (failAtWindowEnd)
                await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "CloseWindowAsync"));

            fixture.Stream.Dispose();
            fixture.Transport.Error = null;
            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(EngineInternals.Field(fixture.Stream, "_held"), Is.Null);
                Assert.That(EngineInternals.Field(fixture.Stream, "_heldLength"), Is.Zero);
                Assert.That(
                    held
                        .AsSpan(0, length)
                        .IndexOfAnyExcept((byte)0),
                    Is.EqualTo(-1)
                );
                Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.EqualTo(1));
                Assert.That(fixture.Transport.Length, Is.Zero);
            }
        }

        [Test]
        public async Task AbandonedOutputReleasesHeldBytesWhenItsTransportIsClosed()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("abandoned"), 0, 9);
            var held =
                (byte[])(EngineInternals.Field(fixture.Stream, "_held")
                    ?? throw new AssertionException("Missing held output lease."));
            var length =
                (int)(EngineInternals.Field(fixture.Stream, "_heldLength")
                    ?? throw new AssertionException("Missing held output length."));
            fixture.Transport.Dispose();
            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(EngineInternals.Field(fixture.Stream, "_held"), Is.Null);
                Assert.That(EngineInternals.Field(fixture.Stream, "_heldLength"), Is.Zero);
                Assert.That(
                    held
                        .AsSpan(0, length)
                        .IndexOfAnyExcept((byte)0),
                    Is.EqualTo(-1)
                );
                Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.Zero);
            }
        }

        private static string Body(Issue574_ResponseWrites.RecordingTransport transport)
        {
            var wire = Encoding.ASCII.GetString(transport.ToArray());
            var boundary = wire.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            if (boundary <= 0) throw new AssertionException("Missing complete response head.");

            return wire[(boundary + 4)..];
        }

        private static readonly Type Heartbeat = EngineInternals.Type("EmbedIO.Net.Internal.Http1OutputHeartbeat");

        private static object? MonitoredCount =>
            Heartbeat
                .GetProperty("MonitoredCount", EngineInternals.Members)
                ?.GetValue(null);

        private static IDisposable OpenWindow(Stream stream)
        {
            var registration = EngineInternals.Create("EmbedIO.Net.Internal.Http1OutputHeartbeat+Registration");
            EngineInternals.Call(stream, "OpenWindow", registration);
            return (IDisposable)registration;
        }

        private sealed class SlowHeartbeat : IDisposable
        {
            private readonly System.Reflection.PropertyInfo _interval = Heartbeat.GetProperty(
                "Interval",
                EngineInternals.Members
            )
                ?? throw new AssertionException("Missing output heartbeat interval.");
            private readonly object _previous;

            internal SlowHeartbeat()
            {
                Assert.That(MonitoredCount, Is.Zero);
                _previous = _interval.GetValue(null)
                    ?? throw new AssertionException("Missing output heartbeat interval value.");
                _interval.SetValue(null, TimeSpan.FromSeconds(1));
            }

            public void Dispose() => _interval.SetValue(null, _previous);
        }
    }
}
