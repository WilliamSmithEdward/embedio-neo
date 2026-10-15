using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2FrameTest
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public async Task CommittedBatchRetainsEveryTransportCancellationSource(int canceledSource)
        {
            using var connectionStop = new CancellationTokenSource();
            using var firstDeadline = new CancellationTokenSource();
            using var secondDeadline = new CancellationTokenSource();
            using var output = new BatchCancellationOutput();
            using var transport = (IDisposable)Transport(output);
            (TransportType.GetProperty("ConnectionToken", Flags) ?? throw new AssertionException("Missing transport cancellation."))
                .SetValue(transport, connectionStop.Token);
            var leading = Write(transport, Frame(6, 0, 0, new byte[8]));
            Task first = Task.CompletedTask;
            Task second = Task.CompletedTask;
            try
            {
                await output.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                first = WriteWithDeadline(transport, firstDeadline.Token);
                second = WriteWithDeadline(transport, secondDeadline.Token);
                output.ReleaseFirst.TrySetResult(true);
                await output.BatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(output.BatchLength, Is.EqualTo(34), "Both PING frames must commit in one transport write.");
                var canceled = canceledSource == 0 ? connectionStop : canceledSource == 1 ? firstDeadline : secondDeadline;
                canceled.Cancel();
                Assert.That(output.BatchToken.IsCancellationRequested, Is.True,
                    "Every source must remain linked until the shared transport write completes.");
                await Assert.ThatAsync(async () => await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2)),
                    Throws.InstanceOf<OperationCanceledException>());
            }
            finally
            {
                // Release both barriers even on the unfixed implementation so no output actor survives the test.
                output.ReleaseFirst.TrySetResult(true);
                output.ReleaseBatch.TrySetResult(true);
                try { await Task.WhenAll(leading, first, second).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (OperationCanceledException) { }
            }
        }

        private static Task WriteWithDeadline(object transport, CancellationToken token)
        {
            var frames = Array.CreateInstance(FrameType, 1);
            frames.SetValue(Frame(6, 0, 0, new byte[8]), 0);
            return (Task)((TransportType.GetMethod("WriteAsync", Flags) ?? throw new AssertionException("Missing frame writer."))
                .Invoke(transport, new object[] { frames, 16384, token }) ?? throw new AssertionException("Missing write task."));
        }

        private sealed class BatchCancellationOutput : MemoryStream
        {
            private int _writes;
            internal TaskCompletionSource<bool> FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> BatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> ReleaseBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationToken BatchToken { get; private set; }
            internal int BatchLength { get; private set; }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Interlocked.Increment(ref _writes) == 1)
                {
                    FirstStarted.TrySetResult(true);
                    await ReleaseFirst.Task.WaitAsync(token);
                }
                else
                {
                    BatchToken = token;
                    BatchLength = count;
                    BatchStarted.TrySetResult(true);
                    await ReleaseBatch.Task.WaitAsync(token);
                }
                await base.WriteAsync(buffer, offset, count, token);
            }
        }
    }
}
