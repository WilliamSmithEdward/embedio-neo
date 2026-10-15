using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class RequestBodyRateStreamLifecycleTest
    {
        private const BindingFlags InstanceHidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags StaticHidden = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
        private static readonly Type MonitorType = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.RequestBodyRateStream")
            ?? throw new AssertionException("Missing rate monitor.");
        private static readonly Type HeartbeatType = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.RequestBodyRateHeartbeat")
            ?? throw new AssertionException("Missing rate heartbeat.");

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task PreviousCallerCannotCancelLaterRead(bool memory, bool nextCanCancel)
        {
            using var source = new TwoReadSource();
            using var monitor = CreateMonitor(source);
            using var firstCaller = new CancellationTokenSource();
            using var nextCaller = new CancellationTokenSource();
            var buffer = new byte[1];
            Assert.That(await ReadAsync(monitor, buffer, firstCaller.Token, memory), Is.EqualTo(1));

            var pending = ReadAsync(monitor, buffer, nextCanCancel ? nextCaller.Token : CancellationToken.None, memory);
            try
            {
                await source.Waiting.Task.WaitAsync(Deadline);
                firstCaller.Cancel();
                source.Supply.TrySetResult(1);
                Assert.That(await pending.WaitAsync(Deadline), Is.EqualTo(1),
                    "Cancellation belongs to the completed read, not the later caller.");
            }
            finally
            {
                source.Supply.TrySetResult(1);
                try { await pending.WaitAsync(Deadline); }
                catch (OperationCanceledException) { }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CallerTransitionWaitsForEarlierCancellationCallback(bool memory)
        {
            using var source = new TwoReadSource();
            using var monitor = CreateMonitor(source);
            using var firstCaller = new CancellationTokenSource();
            using var callbackEntered = new ManualResetEventSlim();
            using var releaseCallback = new ManualResetEventSlim();
            var buffer = new byte[1];
            Assert.That(await ReadAsync(monitor, buffer, firstCaller.Token, memory), Is.EqualTo(1));
            var readSource = (CancellationTokenSource)(MonitorType.GetField("_readSource", InstanceHidden)?.GetValue(monitor)
                ?? throw new AssertionException("Missing read cancellation source."));
            var link = MonitorType.GetField("_link", InstanceHidden)
                ?? throw new AssertionException("Missing caller registration.");
            using var blockingCallback = readSource.Token.Register(() =>
            {
                callbackEntered.Set();
                if (!releaseCallback.Wait(Deadline)) throw new TimeoutException("Cancellation callback was not released.");
            });
            var cancellation = Task.Run(firstCaller.Cancel);
            Task<int>? pending = null;
            try
            {
                Assert.That(callbackEntered.Wait(Deadline), Is.True);
                pending = Task.Run(() => ReadAsync(monitor, buffer, CancellationToken.None, memory));
                Assert.That(SpinWait.SpinUntil(() => link.GetValue(monitor) == null, Deadline), Is.True,
                    "The new caller must detach the old registration before reusing its source.");
                releaseCallback.Set();
                await cancellation.WaitAsync(Deadline);
                await source.Waiting.Task.WaitAsync(Deadline);
                source.Supply.TrySetResult(1);
                Assert.That(await pending.WaitAsync(Deadline), Is.EqualTo(1));
            }
            finally
            {
                releaseCallback.Set();
                source.Supply.TrySetResult(1);
                await cancellation.WaitAsync(Deadline);
                if (pending != null)
                {
                    try { await pending.WaitAsync(Deadline); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        [TestCase("Retire")]
        [TestCase("Suspend")]
        public async Task EndingMonitoringDuringRegistrationDoesNotRetainBody(string operation)
        {
            using var monitor = CreateMonitor(new MemoryStream());
            var sync = HeartbeatType.GetField("Sync", StaticHidden)?.GetValue(null)
                ?? throw new AssertionException("Missing heartbeat synchronization.");
            var registered = MonitorType.GetField("_registered", InstanceHidden)
                ?? throw new AssertionException("Missing registration state.");
            var register = MonitorType.GetMethod("Register", InstanceHidden)
                ?? throw new AssertionException("Missing registration method.");
            var end = MonitorType.GetMethod(operation, InstanceHidden)
                ?? throw new AssertionException("Missing lifecycle method.");
            var remove = HeartbeatType.GetMethod("Remove", StaticHidden)
                ?? throw new AssertionException("Missing heartbeat removal.");
            var count = HeartbeatType.GetProperty("MonitoredCount", StaticHidden)
                ?? throw new AssertionException("Missing monitored count.");
            var before = count.GetValue(null);
            Task? registration = null;
            try
            {
                lock (sync)
                {
                    // Add cannot acquire Sync until the lifecycle operation has completed.
                    // Its preceding state transition is visible independently of that lock.
                    registration = Task.Run(() => register.Invoke(monitor, null));
                    Assert.That(SpinWait.SpinUntil(() => (int)(registered.GetValue(monitor) ?? 0) != 0, Deadline), Is.True);
                    end.Invoke(monitor, null);
                }
                await registration.WaitAsync(Deadline);
                Assert.That(count.GetValue(null), Is.EqualTo(before),
                    "A body whose monitoring ended must not remain rooted in the heartbeat.");
            }
            finally
            {
                if (registration != null) await registration.WaitAsync(Deadline);
                // Also clean up the uncorrected implementation's leaked registration.
                remove.Invoke(null, new object[] { monitor });
            }
        }

        private static Stream CreateMonitor(Stream source)
            => (Stream)(Activator.CreateInstance(MonitorType, InstanceHidden, null,
                new object?[] { source, new MinimumDataRate(1000, TimeSpan.FromHours(1)), null, null }, null)
                ?? throw new AssertionException("Could not construct rate monitor."));

        private static Task<int> ReadAsync(Stream stream, byte[] buffer, CancellationToken token, bool memory)
            => memory ? stream.ReadAsync(buffer.AsMemory(), token).AsTask() : stream.ReadAsync(buffer, 0, buffer.Length, token);

        private sealed class TwoReadSource : Stream
        {
            private int _reads;
            internal TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<int> Supply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _reads) == 1) return Task.FromResult(1);
                var read = Supply.Task.WaitAsync(cancellationToken);
                Waiting.TrySetResult(true);
                return read;
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => new(ReadAsync(Array.Empty<byte>(), 0, 0, cancellationToken));
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
