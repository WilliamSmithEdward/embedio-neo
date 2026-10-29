using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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

        [Test]
        public void NeverActiveConnectionsDoNotEnterTheOutputWatchlistOrStartItsTimer()
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            List<IDisposable> registrations = [];

            try
            {
                for (var index = 0; index < 512; index++)
                    registrations.Add(
                        (IDisposable)EngineInternals.Create("EmbedIO.Net.Internal.Http1OutputHeartbeat+Registration")
                    );

                TickHeartbeat();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(MonitoredCount, Is.EqualTo(512));
                    Assert.That(ActiveCount, Is.Zero);
                    Assert.That(HeartbeatRunning, Is.Zero);
                }

                foreach (var registration in registrations)
                    Assert.That(EngineInternals.Field(registration, "_slot"), Is.EqualTo(-1));
            }

            finally
            {
                foreach (var registration in registrations) registration.Dispose();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(MonitoredCount, Is.Zero);
                Assert.That(ActiveCount, Is.Zero);
            }
        }

        [Test]
        public async Task AQuiescentConnectionIsPrunedWhileAnotherConnectionStillHoldsOutput()
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var quiet = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var held = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var quietRegistration = OpenWindow(quiet.Stream);
            using var heldRegistration = OpenWindow(held.Stream);
            quiet.Stream.Write(Encoding.ASCII.GetBytes("AAA"), 0, 3);
            held.Stream.Write(Encoding.ASCII.GetBytes("BBB"), 0, 3);
            quiet.Stream.Dispose();
            Assert.That(ActiveCount, Is.EqualTo(2));
            TickHeartbeat();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(MonitoredCount, Is.EqualTo(2));
                Assert.That(ActiveCount, Is.EqualTo(1));
                Assert.That(EngineInternals.Field(quietRegistration, "_slot"), Is.EqualTo(-1));
                Assert.That(EngineInternals.Field(heldRegistration, "_output"), Is.SameAs(held.Stream));
                Assert.That(held.Transport.AsyncWrites, Is.Zero);
                Assert.That(HeartbeatRunning, Is.EqualTo(1));
            }

            TickHeartbeat();
            Assert.That(Body(held.Transport), Is.EqualTo("3\r\nBBB\r\n"));
            TickHeartbeat();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ActiveCount, Is.Zero);
                Assert.That(HeartbeatRunning, Is.Zero);
                Assert.That(MonitoredCount, Is.EqualTo(2));
            }
        }

        [Test]
        public async Task ConcurrentPruningAndReactivationRetainHeldOutputAndItsTimerWakeup()
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            byte[] payload = [(byte)'X'];
            fixture.Stream.Write(payload, 0, payload.Length);

            for (var round = 0; round < 64; round++)
            {
                await SendDueHeartbeatAsync(fixture.Stream);
                using var start = new ManualResetEventSlim();
                var pruning = Task.Run(() =>
                {
                    start.Wait();
                    TickHeartbeat();
                });
                var holding = Task.Run(() =>
                {
                    start.Wait();
                    fixture.Stream.Write(payload, 0, payload.Length);
                });
                start.Set();
                await Task
                    .WhenAll(pruning, holding)
                    .WaitAsync(TimeSpan.FromSeconds(5));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(ActiveCount, Is.EqualTo(1));
                    Assert.That(HeartbeatRunning, Is.EqualTo(1));
                    Assert.That(EngineInternals.Field(registration, "_output"), Is.SameAs(fixture.Stream));
                }
            }

            await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "CloseWindowAsync"));
            TickHeartbeat();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ActiveCount, Is.Zero);
                Assert.That(HeartbeatRunning, Is.Zero);
            }
        }

        [Test]
        public async Task ANewBusyEpochRestartsTheTimerAndRetirementRemovesTheConnectionImmediately()
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("AAA"), 0, 3);
            await SendDueHeartbeatAsync(fixture.Stream);
            Assert.That(ActiveCount, Is.EqualTo(1));
            TickHeartbeat();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ActiveCount, Is.Zero);
                Assert.That(HeartbeatRunning, Is.Zero);
            }

            fixture.Stream.Write(Encoding.ASCII.GetBytes("BBB"), 0, 3);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ActiveCount, Is.EqualTo(1));
                Assert.That(HeartbeatRunning, Is.EqualTo(1));
            }

            registration.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ActiveCount, Is.Zero);
                Assert.That(MonitoredCount, Is.Zero);
                Assert.That(HeartbeatRunning, Is.Zero);
                Assert.That(EngineInternals.Field(registration, "_output"), Is.Null);
            }

            await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "AbandonAsync"));
            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo("3\r\nAAA\r\n"));
        }

        [Test]
        public async Task RetiringAMiddleActiveConnectionKeepsOtherWatchlistSlotsValid()
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var first = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var middle = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var last = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var firstRegistration = OpenWindow(first.Stream);
            using var middleRegistration = OpenWindow(middle.Stream);
            using var lastRegistration = OpenWindow(last.Stream);
            first.Stream.Write(Encoding.ASCII.GetBytes("AAA"), 0, 3);
            middle.Stream.Write(Encoding.ASCII.GetBytes("BBB"), 0, 3);
            last.Stream.Write(Encoding.ASCII.GetBytes("CCC"), 0, 3);
            Assert.That(ActiveCount, Is.EqualTo(3));
            middleRegistration.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ActiveCount, Is.EqualTo(2));
                Assert.That(MonitoredCount, Is.EqualTo(2));
                Assert.That(EngineInternals.Field(middleRegistration, "_slot"), Is.EqualTo(-1));
                Assert.That(EngineInternals.Field(lastRegistration, "_slot"), Is.EqualTo(1));
            }

            TickHeartbeat();
            TickHeartbeat();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Body(first.Transport), Is.EqualTo("3\r\nAAA\r\n"));
                Assert.That(Body(last.Transport), Is.EqualTo("3\r\nCCC\r\n"));
                Assert.That(middle.Transport.AsyncWrites, Is.Zero);
            }

            TickHeartbeat();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ActiveCount, Is.Zero);
                Assert.That(HeartbeatRunning, Is.Zero);
            }

            await EngineInternals.AwaitAsync(EngineInternals.Call(middle.Stream, "AbandonAsync"));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task CompletingAsyncWriteDoesNotQueueActorWindowClose(bool heldPrefix, bool failWrite)
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            byte[]? lease = null;
            var leaseLength = 0;

            if (heldPrefix)
            {
                fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
                lease =
                    (byte[])(EngineInternals.Field(fixture.Stream, "_held")
                        ?? throw new AssertionException("Missing held output lease."));
                leaseLength = (int)(EngineInternals.Field(fixture.Stream, "_heldLength")
                    ?? throw new AssertionException("Missing held output length."))
                    + 3;
            }

            fixture.Transport.BlockWrites = true;

            if (failWrite) fixture.Transport.Error = new IOException("controlled completing write failure");

            var bytes = Encoding.ASCII.GetBytes(heldPrefix ? "DEF" : "ABCDEF");
            var writing = fixture.Stream.WriteAsync(bytes, 0, bytes.Length);
            await VerifyActorCloseDuringCompletingIoAsync(
                fixture,
                writing,
                failWrite ? null : "ABCDEF",
                lease,
                leaseLength
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompletingFlushDoesNotQueueActorWindowClose(bool suppressed)
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false, ignoreErrors: true);

            if (suppressed) fixture.Response.StatusCode = 204;

            else fixture.Response.ContentLength64 = 0;

            using var registration = OpenWindow(fixture.Stream);
            fixture.Transport.BlockWrites = true;
            var flushing = fixture.Stream.FlushAsync();
            await VerifyActorCloseDuringCompletingIoAsync(fixture, flushing, "", null, 0);
        }

        [Test]
        public async Task PartialOutputStillWaitsForActorWindowDrain()
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            fixture.Transport.BlockWrites = true;
            var gate = EngineInternals.Field(fixture.Stream, "_writers")
                ?? throw new AssertionException("Missing response write gate.");
            var writer =
                (IDisposable)(EngineInternals.Call(gate, "Enter")
                    ?? throw new AssertionException("Missing admitted writer scope."));
            var closing = CloseActorWindow(fixture.Stream);
            var writerReleased = false;

            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(closing.IsCompleted, Is.False);
                    Assert.That(EngineInternals.Field(gate, "_state"), Is.EqualTo(2));
                }

                writer.Dispose();
                writerReleased = true;
                await fixture.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(
                    closing.IsCompleted,
                    Is.False,
                    "Closing a partial window must await its actual held-byte submission."
                );
            }

            finally
            {
                if (!writerReleased) writer.Dispose();

                fixture.Transport.BlockWrites = false;
                fixture.Transport.ReleaseWrites.TrySetResult();
                await closing
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.That(Body(fixture.Transport), Is.EqualTo("ABC"));
            await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("DEF"), 0, 3);
            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Body(fixture.Transport), Is.EqualTo("ABCDEF"));
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(2));
                Assert.That(fixture.Transport.SynchronousWrites, Is.Zero);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RejectedOverlengthWriteDoesNotDisarmInlineWindow(bool asynchronous)
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            var bytes = Encoding.ASCII.GetBytes("DEFG");

            if (asynchronous)
                await Assert.ThrowsAsync<ProtocolViolationException>(
                    async () => await fixture.Stream.WriteAsync(bytes, 0, bytes.Length)
                );

            else
                Assert.Throws<ProtocolViolationException>(() => fixture.Stream.Write(bytes, 0, bytes.Length));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(EngineInternals.Field(fixture.Stream, "_window"), Is.True);
                Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.Zero);
            }

            await CloseActorWindow(fixture.Stream);
            Assert.That(Body(fixture.Transport), Is.EqualTo("ABC"));
            await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("DEF"), 0, 3);
            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo("ABCDEF"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CanceledCompletingWriteDoesNotDisarmInlineWindow(bool queued)
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            var gate = EngineInternals.Field(fixture.Stream, "_writers")
                ?? throw new AssertionException("Missing response write gate.");
            var writer = queued ? (IDisposable?)EngineInternals.Call(gate, "Enter") : null;
            using var cancellation = new CancellationTokenSource();

            try
            {
                if (!queued) cancellation.Cancel();

                var writing = fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("DEF"), 0, 3, cancellation.Token);

                if (queued)
                {
                    Assert.That(writing.IsCompleted, Is.False);
                    cancellation.Cancel();
                }

                await Assert.CatchAsync<OperationCanceledException>(async () => await writing);
            }

            finally
            {
                cancellation.Cancel();
                writer?.Dispose();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(EngineInternals.Field(fixture.Stream, "_window"), Is.True);
                Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.Zero);
            }

            await CloseActorWindow(fixture.Stream);
            Assert.That(Body(fixture.Transport), Is.EqualTo("ABC"));
            await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("DEF"), 0, 3);
            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo("ABCDEF"));
        }

        [Test]
        public async Task CommittedErrorStillWaitsForAnActorWindowDrainAlreadyInProgress()
        {
            using var slowHeartbeat = new SlowHeartbeat(10000);
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors: true);
            using var registration = OpenWindow(fixture.Stream);
            EngineInternals.SetField(fixture.Response, "_output", fixture.Stream);
            fixture.Stream.Write(Encoding.ASCII.GetBytes("before"), 0, 6);
            fixture.Transport.BlockWrites = true;
            var actorClosing = CloseActorWindow(fixture.Stream);
            await fixture.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var errorClosing = EngineInternals.AwaitAsync(
                EngineInternals.Call(fixture.Response, "AbortCommittedAsync")
            );
            var errorWasPending = !errorClosing.IsCompleted;
            var gate = EngineInternals.Field(fixture.Stream, "_writers")
                ?? throw new AssertionException("Missing response write gate.");
            var usersDuringError = EngineInternals.Field(gate, "_state");

            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(EngineInternals.Field(fixture.Stream, "_window"), Is.False);
                    Assert.That(fixture.Transport.Length, Is.Zero);
                }
            }

            finally
            {
                fixture.Transport.BlockWrites = false;
                fixture.Transport.ReleaseWrites.TrySetResult();
                await actorClosing
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                await errorClosing.WaitAsync(TimeSpan.FromSeconds(5));
                await EngineInternals.AwaitAsync(EngineInternals.Call(fixture.Stream, "AbandonAsync"));
            }

            fixture.Stream.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    errorWasPending,
                    Is.True,
                    "A committed error must await the drain that already owns accepted bytes."
                );
                Assert.That(usersDuringError, Is.EqualTo(2));
                Assert.That(Body(fixture.Transport), Is.EqualTo("6\r\nbefore\r\n"));
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
                Assert.That(fixture.Transport.SynchronousWrites, Is.Zero);
                Assert.That(EngineInternals.Field(fixture.Stream, "_held"), Is.Null);
            }
        }

        private static ValueTask CloseActorWindow(Stream output)
        {
            // The before-change proof uses the actor's old entry point, preserving the
            // same behavioral assertion instead of failing on a missing new method.
            var name = output
                .GetType()
                .GetMethod("CloseInlineWindowAsync", EngineInternals.Members) is null
                ? "CloseWindowAsync"
                : "CloseInlineWindowAsync";
            return (ValueTask)(EngineInternals.Call(output, name)
                ?? throw new AssertionException("Missing actor window close operation."));
        }

        private static async Task VerifyActorCloseDuringCompletingIoAsync(
            Issue574_ResponseWrites.Fixture fixture,
            Task writing,
            string? expectedBody,
            byte[]? heldLease,
            int leaseLength
        )
        {
            await fixture.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var gate = EngineInternals.Field(fixture.Stream, "_writers")
                ?? throw new AssertionException("Missing response write gate.");
            var closing = CloseActorWindow(fixture.Stream);
            var actorCompletedSynchronously = closing.IsCompletedSuccessfully;
            var usersAfterActorClose = EngineInternals.Field(gate, "_state");
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

            var disposing = new Thread(Complete) { IsBackground = true };
            disposing.Start();

            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(
                        () => (disposing.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        Is.True.After(5000, 10),
                        "Real Dispose must still wait for the actual transport write."
                    );
                    Assert.That(completed.Task.IsCompleted, Is.False);
                }

                if (heldLease is not null)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(EngineInternals.Field(fixture.Stream, "_held"), Is.SameAs(heldLease));
                        Assert.That(Encoding.ASCII.GetString(heldLease, 0, leaseLength), Does.EndWith("ABCDEF"));
                    }
                }
            }

            finally
            {
                fixture.Transport.BlockWrites = false;
                fixture.Transport.ReleaseWrites.TrySetResult();
                await writing.WaitAsync(TimeSpan.FromSeconds(5));
                await closing
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(disposing.Join(TimeSpan.FromSeconds(5)), Is.True);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    actorCompletedSynchronously,
                    Is.True,
                    "The actor must not queue a no-op waiter behind a write that owns completion."
                );
                Assert.That(usersAfterActorClose, Is.EqualTo(1));
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
                Assert.That(fixture.Transport.SynchronousWrites, Is.Zero);
                Assert.That(EngineInternals.Field(fixture.Stream, "_held"), Is.Null);
            }

            if (expectedBody is null) Assert.That(fixture.Transport.Length, Is.Zero);

            else Assert.That(Body(fixture.Transport), Is.EqualTo(expectedBody));

            if (heldLease is not null)
                Assert.That(
                    heldLease
                        .AsSpan(0, leaseLength)
                        .IndexOfAnyExcept((byte)0),
                    Is.EqualTo(-1)
                );
        }

        private static int HeartbeatRunning =>
            (int)(Heartbeat
                .GetField("_running", EngineInternals.Members)
                ?.GetValue(null)
                ?? throw new AssertionException("Missing output heartbeat running state."));

        private static void TickHeartbeat() => _ = EngineInternals.CallStatic(Heartbeat, "Tick", [null]);

        private static Task<object?> SendDueHeartbeatAsync(Stream output)
        {
            var tick =
                (int)(EngineInternals.Field(output, "_heldTick")
                    ?? throw new AssertionException("Missing held output tick."));
            return EngineInternals.AwaitAsync(EngineInternals.Call(output, "SendHeartbeatAsync", unchecked(tick + 2)));
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
