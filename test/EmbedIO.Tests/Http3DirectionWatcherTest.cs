using System;
using System.IO;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public class Http3DirectionWatcherTest
    {
        private static MethodInfo Method(string name)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection");
            if (type == null) Assert.Ignore("The legacy asset has no direct QUIC transport.");
            return (type ?? throw new AssertionException("Missing QUIC connection."))
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing request-direction member " + name + ".");
        }

        private static void Watch(Task direction, CancellationTokenSource requestStop)
            => Method("WatchRequestDirection").Invoke(null, new object[] { direction, requestStop });

        // What the request owner sees for this direction when the request ends.
        private static Exception? Unexpected(Task direction, CancellationTokenSource requestStop)
            => (Exception?)Method("UnexpectedDirectionFault").Invoke(null, new object[] { direction, requestStop });

        [TestCase(false)]
        [TestCase(true)]
        public void SuccessfulFinDoesNotCancelTheRequest(bool alreadyCompleted)
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource();
            if (alreadyCompleted) direction.TrySetResult();
            Watch(direction.Task, requestStop);
            direction.TrySetResult();
            Assert.That(requestStop.IsCancellationRequested, Is.False);
            Assert.That(Unexpected(direction.Task, requestStop), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TransportFaultCancelsTheRequest(bool alreadyFaulted)
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource();
            var reset = new QuicException(QuicError.StreamAborted, 0x10c, "Peer reset.");
            if (alreadyFaulted) direction.TrySetException(reset);
            Watch(direction.Task, requestStop);
            direction.TrySetException(reset);
            // The continuation may be queued rather than inlined under a synchronization context.
            Assert.That(SpinWait.SpinUntil(() => requestStop.IsCancellationRequested, TimeSpan.FromSeconds(2)), Is.True, "An early upload reset must cancel the running request.");
            Assert.That(Unexpected(direction.Task, requestStop), Is.Null, "The cancellation is the reset's only effect.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EndingTheRequestDoesNotCompleteTheTransportDirection(bool alreadyStopped)
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource();
            if (alreadyStopped) requestStop.Cancel();
            Watch(direction.Task, requestStop);
            requestStop.Cancel();
            Assert.That(direction.Task.IsCompleted, Is.False);
            Assert.That(Unexpected(direction.Task, requestStop), Is.Null, "An unfinished direction is not a failure.");
            direction.TrySetResult();
        }

        [Test]
        public void DirectionEndingAfterTheRequestHasNoFurtherEffect()
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource();
            var observed = 0;
            using var registration = requestStop.Token.Register(() => Interlocked.Increment(ref observed));
            Watch(direction.Task, requestStop);
            requestStop.Cancel();
            direction.TrySetException(new QuicException(QuicError.ConnectionAborted, 0x100, "Connection closed."));
            Assert.That(Volatile.Read(ref observed), Is.EqualTo(1), "Request callbacks run once.");
        }

        // A transport fault racing the end of the request (which cancels the
        // request source) cancels exactly once and never reports a failure.
        // Request sources are not disposed, as in the connection.
        [Test]
        public async Task DirectionFaultRacingRequestEndCancelsOnce()
        {
            for (var i = 0; i < 2000; ++i)
            {
                var requestStop = new CancellationTokenSource();
                var direction = new TaskCompletionSource();
                var observed = 0;
                requestStop.Token.Register(() => Interlocked.Increment(ref observed));
                Watch(direction.Task, requestStop);
                using var start = new Barrier(2);
                var fault = Task.Run(() => { start.SignalAndWait(); direction.TrySetException(new QuicException(QuicError.StreamAborted, 0x10c, "Peer reset.")); });
                var end = Task.Run(() => { start.SignalAndWait(); requestStop.Cancel(); });
                await Task.WhenAll(fault, end);
                Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref observed) == 1, TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(Unexpected(direction.Task, requestStop), Is.Null);
            }
        }

        [Test]
        public void UnexpectedDirectionFaultRemainsVisibleToTheOwner()
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource();
            Watch(direction.Task, requestStop);
            var failure = new IOException("Unexpected direction failure.");
            direction.TrySetException(failure);
            Assert.That(requestStop.IsCancellationRequested, Is.False);
            Assert.That(Unexpected(direction.Task, requestStop), Is.SameAs(failure));
        }

        [Test]
        public void CancellationAfterTheRequestStoppedIsNotAFailure()
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource();
            Watch(direction.Task, requestStop);
            requestStop.Cancel();
            direction.TrySetCanceled(requestStop.Token);
            Assert.That(Unexpected(direction.Task, requestStop), Is.Null);
        }
    }
}
