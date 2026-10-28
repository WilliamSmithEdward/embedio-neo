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
    public partial class Http1CoalescedOutputTest
    {
        [Test]
        public async Task ABackpressuredHeartbeatDoesNotDelayAnotherConnectionOrQueueDuplicateSenders()
        {
            using var first = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var firstRegistration = OpenWindow(first.Stream);
            first.Transport.BlockWrites = true;
            first.Stream.Write(Encoding.ASCII.GetBytes("AAA"), 0, 3);
            await first.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            try
            {
                using var second = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
                using var secondRegistration = OpenWindow(second.Stream);
                second.Stream.Write(Encoding.ASCII.GetBytes("BBB"), 0, 3);
                await second.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(() => EngineInternals.Field(secondRegistration, "_sending"), Is.Zero.After(5000, 10));
                    Assert.That(Body(second.Transport), Is.EqualTo("3\r\nBBB\r\n"));
                }

                for (var tick = 0; tick < 6; tick++)
                    EngineInternals.CallStatic(Heartbeat, "Tick", [null]);

                var gate = EngineInternals.Field(first.Stream, "_writers")
                    ?? throw new AssertionException("Missing response write gate.");

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(first.Transport.AsyncWrites, Is.EqualTo(1));
                    Assert.That(first.Transport.Length, Is.Zero);
                    Assert.That(
                        EngineInternals.Field(gate, "_state"),
                        Is.EqualTo(1),
                        "Repeated ticks must not queue writers behind the same backpressured send."
                    );
                    Assert.That(EngineInternals.Field(firstRegistration, "_sending"), Is.EqualTo(1));
                }
            }

            finally
            {
                first.Transport.BlockWrites = false;
                first.Transport.ReleaseWrites.TrySetResult();
                Assert.That(() => EngineInternals.Field(firstRegistration, "_sending"), Is.Zero.After(5000, 10));
            }
        }

        [Test]
        public async Task AbandoningAnOpenResponseClearsHeldOutputWithoutSendingFinalFraming()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("abandoned secret"), 0, 16);
            var held =
                (byte[])(EngineInternals.Field(fixture.Stream, "_held")
                    ?? throw new AssertionException("Missing held output lease."));
            var length =
                (int)(EngineInternals.Field(fixture.Stream, "_heldLength")
                    ?? throw new AssertionException("Missing held output length."));
            await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "AbandonAsync"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(EngineInternals.Field(fixture.Stream, "_held"), Is.Null);
                Assert.That(
                    held
                        .AsSpan(0, length)
                        .IndexOfAnyExcept((byte)0),
                    Is.EqualTo(-1)
                );
                Assert.That(EngineInternals.Field(registration, "_output"), Is.Null);
            }

            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.Zero);
                Assert.That(fixture.Transport.Length, Is.Zero);
            }
        }

        [Test]
        public async Task RetiringAConnectionRemovesItsRegistrationAndReleasesUnfinishedHeldOutput()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            var connection = EngineInternals.Field(fixture.Response, "_transport")
                ?? throw new AssertionException("Missing response connection.");
            // Give the real accepted connection the controlled output so retirement exercises
            // its production release path without allowing a heartbeat to write to a peer.
            EngineInternals.SetField(connection, "_oStream", fixture.Stream);
            EngineInternals.SetField(connection, "_outputHeartbeat", registration);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("retired secret"), 0, 14);
            var held =
                (byte[])(EngineInternals.Field(fixture.Stream, "_held")
                    ?? throw new AssertionException("Missing held output lease."));
            var length =
                (int)(EngineInternals.Field(fixture.Stream, "_heldLength")
                    ?? throw new AssertionException("Missing held output length."));
            EngineInternals.Call(connection, "ForceClose");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => EngineInternals.Field(fixture.Stream, "_held"), Is.Null.After(5000, 10));
                Assert.That(
                    held
                        .AsSpan(0, length)
                        .IndexOfAnyExcept((byte)0),
                    Is.EqualTo(-1)
                );
                Assert.That(MonitoredCount, Is.Zero);
                Assert.That(EngineInternals.Field(registration, "_output"), Is.Null);
                Assert.That(
                    Heartbeat
                        .GetField("_running", EngineInternals.Members)
                        ?.GetValue(null),
                    Is.Zero
                );
            }

            fixture.Stream.Dispose();
            Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.Zero);
        }

        [Test]
        public async Task AQueuedHeartbeatRechecksTheAgeOfAReplacementHeldSegment()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("AAA"), 0, 3);
            var heldTick =
                (int)(EngineInternals.Field(fixture.Stream, "_heldTick")
                    ?? throw new AssertionException("Missing held output tick."));
            var snapshotTick = unchecked(heldTick + 2);
            var gate = EngineInternals.Field(fixture.Stream, "_writers")
                ?? throw new AssertionException("Missing response write gate.");
            var writer =
                (IDisposable)(EngineInternals.Call(gate, "Enter")
                    ?? throw new AssertionException("Missing admitted writer scope."));
            Task pending;

            try
            {
                pending = EngineInternals.AwaitAsync(
                    EngineInternals.Call(fixture.Stream, "SendHeartbeatAsync", snapshotTick)
                );
                Assert.That(pending.IsCompleted, Is.False);
                // The admitted application writer drains the previous segment and starts a new
                // one before the queued heartbeat obtains the gate. Advance only its age stamp.
                await EngineInternals.AwaitAsync(
                    EngineInternals.Call(fixture.Stream, "SendHeldAsync", false, CancellationToken.None)
                );
                await EngineInternals.AwaitAsync(
                    EngineInternals.Call(
                        fixture.Stream,
                        "WriteBodyAsync",
                        Encoding.ASCII.GetBytes("BBB"),
                        0,
                        3,
                        true,
                        false,
                        CancellationToken.None
                    )
                );
                EngineInternals.SetField(fixture.Stream, "_heldTick", snapshotTick);
            }

            finally
            {
                writer.Dispose();
            }

            await pending.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
                Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nAAA\r\n"));
                Assert.That(EngineInternals.Field(registration, "_output"), Is.SameAs(fixture.Stream));
            }

            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nAAA\r\n3\r\nBBB\r\n0\r\n\r\n"));
        }

        [Test]
        public async Task TheSharedTimerDoesNotCaptureTheFirstApplicationsAmbientContext()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(MonitoredCount, Is.Zero);
                Assert.That(
                    Heartbeat
                        .GetField("_running", EngineInternals.Members)
                        ?.GetValue(null),
                    Is.Zero
                );
                Assert.That(
                    () => Heartbeat
                        .GetField("_ticking", EngineInternals.Members)
                        ?.GetValue(null),
                    Is.Zero.After(5000, 10)
                );
            }

            var timerField = Heartbeat.GetField("_timer", EngineInternals.Members)
                ?? throw new AssertionException("Missing shared output timer.");
            var priorTimer = timerField.GetValue(null);
            var ambient = new AsyncLocal<string?>();
            Timer? createdTimer = null;
            timerField.SetValue(null, null);

            try
            {
                using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
                using var transport = new AmbientTransport(ambient);
                using var output = (Stream)EngineInternals.Create(
                    "EmbedIO.Net.Internal.ResponseStream",
                    transport,
                    fixture.Response,
                    true
                );
                using var registration = OpenWindow(output);
                ambient.Value = "private first application context";
                output.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
                createdTimer = (Timer?)timerField.GetValue(null);
                Assert.That(createdTimer, Is.Not.Null);
                ambient.Value = null;
                Assert.That(
                    await transport.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                    Is.Null,
                    "The process-wide timer must not retain or run under the first application's ambient context."
                );
                output.Dispose();
            }

            finally
            {
                ambient.Value = null;
                createdTimer?.Dispose();
                timerField.SetValue(null, priorTimer);
            }
        }

        [Test]
        public async Task AbandoningDuringTheCompletedButNotYetRetiredGateGapPreservesSuccessfulOutput()
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false, ignoreErrors: true);
            fixture.Response.ContentLength64 = 3;
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            // Dispose publishes these fields while still owning the live gate, then releases
            // the gate before retiring it. Model that narrow gap with the actual writer state.
            EngineInternals.SetField(fixture.Stream, "_closed", 1);
            EngineInternals.SetField(fixture.Stream, "_completionFinished", 1);

            try
            {
                await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "AbandonAsync"));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(EngineInternals.Property(fixture.Stream, "OutputCompleted"), Is.True);
                    Assert.That(EngineInternals.Property(fixture.Stream, "HasOutputFailure"), Is.False);
                    Assert.That(Body(fixture.Transport), Is.EqualTo("ABC"));
                }
            }

            finally
            {
                EngineInternals.SetField(fixture.Stream, "_closed", 0);
                EngineInternals.SetField(fixture.Stream, "_completionFinished", 0);
            }
        }

        [Test]
        public async Task AnAbortPreservationAttemptDoesNotWaitOrQueueWhenTheWriteGateIsOwned()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            var gate = EngineInternals.Field(fixture.Stream, "_writers")
                ?? throw new AssertionException("Missing response write gate.");
            var writer =
                (IDisposable)(EngineInternals.Call(gate, "Enter")
                    ?? throw new AssertionException("Missing admitted writer scope."));
            var attempting = Task.Run(() => EngineInternals.Call(fixture.Stream, "TrySendHeldBeforeAbort"));

            try
            {
                await attempting.WaitAsync(TimeSpan.FromSeconds(2));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(
                        EngineInternals.Field(gate, "_state"),
                        Is.EqualTo(1),
                        "A failed try must not leave a queued writer."
                    );
                    Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.Zero);
                    Assert.That(EngineInternals.Field(registration, "_output"), Is.SameAs(fixture.Stream));
                    Assert.That(EngineInternals.Field(fixture.Stream, "_window"), Is.True);
                }
            }

            finally
            {
                writer.Dispose();
                await attempting.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task AbortPreservationReturnsBeforeBackpressuredIoAndKeepsItsLeaseUntilTransportClose()
        {
            using var slowHeartbeat = new SlowHeartbeat();
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var transport = new CloseReleasedTransport();
            using var output = (Stream)EngineInternals.Create(
                "EmbedIO.Net.Internal.ResponseStream",
                transport,
                fixture.Response,
                true
            );
            using var registration = OpenWindow(output);
            output.Write(Encoding.ASCII.GetBytes("held until closed"), 0, 17);
            var held =
                (byte[])(EngineInternals.Field(output, "_held")
                    ?? throw new AssertionException("Missing held output lease."));
            var length =
                (int)(EngineInternals.Field(output, "_heldLength")
                    ?? throw new AssertionException("Missing held output length."));
            var attempting = Task.Run(() => EngineInternals.Call(output, "TrySendHeldBeforeAbort"));

            try
            {
                await attempting.WaitAsync(TimeSpan.FromSeconds(2));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(transport.Entered.Task.IsCompleted, Is.True);
                    Assert.That(transport.Closed.Task.IsCompleted, Is.False);
                    Assert.That(transport.AsyncWrites, Is.EqualTo(1));
                    Assert.That(EngineInternals.Field(output, "_held"), Is.SameAs(held));
                    Assert.That(
                        Encoding.ASCII.GetString(held, 0, length),
                        Does.Contain("held until closed"),
                        "The in-flight transport still borrows these bytes, so abort must retain their lease."
                    );
                }

                var abandoning = EngineInternals.AwaitAsync(EngineInternals.Call(output, "AbandonAsync"));
                Assert.That(abandoning.IsCompleted, Is.False);
                transport.Dispose();
                await abandoning.WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(EngineInternals.Field(output, "_held"), Is.Null);
                    Assert.That(
                        held
                            .AsSpan(0, length)
                            .IndexOfAnyExcept((byte)0),
                        Is.EqualTo(-1)
                    );
                    Assert.That(EngineInternals.Field(registration, "_output"), Is.Null);
                    Assert.That(EngineInternals.Property(output, "HasOutputFailure"), Is.True);
                }
            }

            finally
            {
                transport.Dispose();
                await attempting.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private sealed class CloseReleasedTransport : MemoryStream
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int AsyncWrites { get; private set; }

            public override async Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken
            )
            {
                AsyncWrites++;
                Entered.TrySetResult();
                await Closed.Task.WaitAsync(cancellationToken);
                throw new ObjectDisposedException(nameof(CloseReleasedTransport));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Closed.TrySetResult();

                base.Dispose(disposing);
            }
        }

        private sealed class AmbientTransport(AsyncLocal<string?> ambient) : MemoryStream
        {
            internal TaskCompletionSource<string?> Observed { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Observed.TrySetResult(ambient.Value);
                cancellationToken.ThrowIfCancellationRequested();
                base.Write(buffer, offset, count);
                return Task.CompletedTask;
            }
        }
    }
}
