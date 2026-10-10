using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class HttpTunnelTest
    {
        [Test]
        public async Task CompletionCallersShareOneBackendOperationAndKeepInputOpen()
        {
            using var stream = new MemoryStream();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            using var tunnel = new HttpTunnel(stream, _ => { Interlocked.Increment(ref calls); return release.Task; }, "test");
            var first = tunnel.CompleteOutputAsync();
            Assert.That(tunnel.CompleteOutputAsync(), Is.SameAs(first));
            Assert.That(first.IsCompleted, Is.False);
            release.TrySetResult();
            await first;
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(stream.CanRead, Is.True, "Send completion must preserve peer input.");
        }

        [Test]
        public async Task IncompleteCapsuleFailsBeforeTheBackendCanSendFin()
        {
            using var stream = new MemoryStream();
            var calls = 0;
            using var tunnel = new HttpTunnel(stream, _ => { calls++; return Task.CompletedTask; }, "test", true);
            var capsules = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
            await capsules.WriteHeaderAsync(0, 3);
            await capsules.WritePayloadAsync(new byte[] { 1 }, 0, 1);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await tunnel.CompleteOutputAsync());
            Assert.That(calls, Is.Zero);
            Assert.That(tunnel.CompleteOutputAsync().IsFaulted, Is.True);
        }

        [Test]
        public async Task PreCanceledCompletionRetainsCanceledTaskStateAndSkipsBackend()
        {
            using var stream = new MemoryStream();
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            var calls = 0;
            using var tunnel = new HttpTunnel(stream, _ => { calls++; return Task.CompletedTask; });
            var completion = tunnel.CompleteOutputAsync(stop.Token);
            await Assert.CatchAsync<OperationCanceledException>(async () => await completion);
            Assert.That(completion.IsCanceled, Is.True);
            Assert.That(calls, Is.Zero);
            Assert.That(tunnel.CompleteOutputAsync(), Is.SameAs(completion));
        }

        [Test]
        public async Task DisposeWaitsForOutputSettlementBeforeClosingOwnedStream()
        {
            using var stream = new DisposalProbe();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tunnel = new HttpTunnel(stream, _ => release.Task);
            tunnel.Dispose();
            Assert.That(stream.Closed.Task.IsCompleted, Is.False);
            release.TrySetResult();
            await stream.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            tunnel.Dispose();
            Assert.That(stream.CloseCount, Is.EqualTo(1));
        }

        [Test]
        public async Task AsyncDisposalCallersShareCompletionAndDisposeExactlyOnce()
        {
            var stream = new DisposalProbe();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tunnel = new HttpTunnel(stream, _ => release.Task);
            var first = tunnel.DisposeAsync().AsTask();
            Assert.That(tunnel.DisposeAsync().AsTask(), Is.SameAs(first));
            tunnel.Dispose();
            Assert.That(stream.Closed.Task.IsCompleted, Is.False);
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(stream.CloseCount, Is.EqualTo(1));
        }

        [Test]
        public async Task CompletionFailureStillDisposesTheStreamAndIsObservable()
        {
            var stream = new DisposalProbe();
            var failure = new IOException("completion failed");
            var tunnel = new HttpTunnel(stream, _ => Task.FromException(failure));
            var error = await Assert.ThrowsAsync<IOException>(async () => await tunnel.DisposeAsync());
            Assert.That(error, Is.SameAs(failure));
            Assert.That(stream.CloseCount, Is.EqualTo(1));
            tunnel.Dispose();
            Assert.That(stream.CloseCount, Is.EqualTo(1));
        }

        [Test]
        public async Task StreamDisposalFailureIsObservableWithoutRepeatingDisposal()
        {
            var stream = new ThrowingDisposalProbe();
            var tunnel = new HttpTunnel(stream, _ => Task.CompletedTask);
            var first = tunnel.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<IOException>(async () => await first);
            Assert.That(tunnel.DisposeAsync().AsTask(), Is.SameAs(first));
            tunnel.Dispose();
            Assert.That(stream.CloseCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ReentrantBackendSeesTheAlreadyPublishedCompletionTask()
        {
            using var stream = new MemoryStream();
            HttpTunnel? tunnel = null;
            Task? nested = null;
            var calls = 0;
            tunnel = new HttpTunnel(stream, _ =>
            {
                calls++;
                nested = (tunnel ?? throw new AssertionException("Missing published tunnel.")).CompleteOutputAsync();
                return Task.CompletedTask;
            });
            using (tunnel)
            {
                var completion = tunnel.CompleteOutputAsync();
                await completion;
                Assert.That(nested, Is.SameAs(completion));
                Assert.That(calls, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task OutputAndStreamDisposalFailuresAreBothPreserved()
        {
            var stream = new ThrowingDisposalProbe();
            var outputFailure = new IOException("output failed");
            var tunnel = new HttpTunnel(stream, _ => Task.FromException(outputFailure));
            var error = await Assert.ThrowsAsync<AggregateException>(async () => await tunnel.CloseAsync())
                ?? throw new AssertionException("Missing combined failure.");
            Assert.That(error.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(error.InnerExceptions[0], Is.SameAs(outputFailure));
            Assert.That(error.InnerExceptions[1], Is.TypeOf<IOException>());
            tunnel.Dispose();
            Assert.That(stream.CloseCount, Is.EqualTo(1));
        }
        private sealed class ThrowingDisposalProbe : MemoryStream
        {
            internal int CloseCount { get; private set; }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing) { CloseCount++; throw new IOException("disposal failed"); }
            }
        }
        private sealed class DisposalProbe : MemoryStream
        {
            internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int CloseCount { get; private set; }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing) { CloseCount++; Closed.TrySetResult(); }
            }
        }
    }
}
