using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http3DirectionWatcherTest
    {
        private static Task Watch(Task direction, CancellationTokenSource requestStop, Task stopped)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection");
            if (type == null) Assert.Ignore("The legacy asset has no direct QUIC transport.");
            var method = (type ?? throw new AssertionException("Missing QUIC connection."))
                .GetMethod("WatchRequestDirectionAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing request-direction watcher.");
            return (Task)(method.Invoke(null, new object[] { direction, requestStop, stopped })
                ?? throw new AssertionException("Missing watcher task."));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SuccessfulFinDoesNotCancelTheRequest(bool alreadyCompleted)
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (alreadyCompleted) direction.TrySetResult();
            var watching = Watch(direction.Task, requestStop, stopped.Task);
            direction.TrySetResult();
            await watching.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(requestStop.IsCancellationRequested, Is.False);
            Assert.That(stopped.Task.IsCompleted, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task StoppingObservationDoesNotCompleteTheTransportDirection(bool alreadyStopped)
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = requestStop.Token.Register(() => stopped.TrySetResult());
            if (alreadyStopped) requestStop.Cancel();
            var watching = Watch(direction.Task, requestStop, stopped.Task);
            requestStop.Cancel();
            await watching.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(direction.Task.IsCompleted, Is.False);
            direction.TrySetResult();
        }

        [Test]
        public async Task UnexpectedDirectionFaultRemainsVisibleToTheOwner()
        {
            using var requestStop = new CancellationTokenSource();
            var direction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watching = Watch(direction.Task, requestStop, stopped.Task);
            var failure = new IOException("Unexpected direction failure.");
            direction.TrySetException(failure);
            var observed = await Assert.ThrowsAsync<IOException>(async () => await watching.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(observed, Is.SameAs(failure));
            Assert.That(requestStop.IsCancellationRequested, Is.False);
        }
    }
}
