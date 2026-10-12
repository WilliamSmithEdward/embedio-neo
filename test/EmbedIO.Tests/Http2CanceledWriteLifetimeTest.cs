using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2CanceledWriteLifetimeTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type ConnectionType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2Connection", true)
            ?? throw new AssertionException("Missing HTTP/2 connection.");
        private static readonly Type FrameType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2Frame", true)
            ?? throw new AssertionException("Missing HTTP/2 frame.");

        [TestCase(false)]
        [TestCase(true)]
        public void CanceledQueuedDataReleasesItsOtherTokenRegistration(bool cancelApplication)
        {
            using var remaining = new CancellationTokenSource();
            var references = CancelQueuedData(remaining.Token, cancelApplication);
            try
            {
                Assert.That(remaining.IsCancellationRequested, Is.False);
                Collect();
                TestContext.Out.WriteLine($"Before remaining token disposal: write alive={references.Write.IsAlive}, connection alive={references.Connection.IsAlive}.");
                Assert.That(references.Write.IsAlive, Is.False,
                    "A canceled write removed from the output queue must not stay rooted by its independent uncanceled token.");
                Assert.That(references.Connection.IsAlive, Is.False,
                    "An independent application token must not retain the retired connection through its canceled write.");
            }
            finally
            {
                // This control removes the suspected external root. It distinguishes
                // registration retention from fixture tasks, JIT locals or static roots.
                remaining.Dispose();
                Collect();
                TestContext.Out.WriteLine($"After remaining token disposal: write alive={references.Write.IsAlive}, connection alive={references.Connection.IsAlive}.");
                Assert.That(references.Write.IsAlive, Is.False,
                    "Disposing the remaining token source must remove the external registration root.");
                Assert.That(references.Connection.IsAlive, Is.False,
                    "The retired owner must collect once the external registration root is removed.");
            }
        }

        // Return only weak references after observing all tasks and disposing the
        // connection/source. Synchronous, non-inlined scope prevents its locals or
        // an outstanding helper state machine from becoming an unrelated GC root.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference Write, WeakReference Connection) CancelQueuedData(CancellationToken remaining, bool cancelApplication)
        {
            using var source = new GatedOutput();
            using var canceled = new CancellationTokenSource();
            using var owner = (IDisposable)(Activator.CreateInstance(ConnectionType, Hidden, null, new object[] { source }, null)
                ?? throw new AssertionException("Missing HTTP/2 connection constructor."));
            var flow = ConnectionType.GetProperty("SendFlow", Hidden)?.GetValue(owner)
                ?? throw new AssertionException("Missing send flow.");
            Call(flow, "Open", 1);
            Assert.That(Call(flow, "TryReserve", 1, 3), Is.EqualTo(3));

            var ping = Activator.CreateInstance(FrameType, Hidden, null, new object[] { (byte)6, (byte)1, 0, new byte[8] }, null)
                ?? throw new AssertionException("Missing PING frame.");
            var frames = Array.CreateInstance(FrameType, 1);
            frames.SetValue(ping, 0);
            var first = (Task)(Call(owner, "SendAsync", frames, CancellationToken.None)
                ?? throw new AssertionException("Missing PING task."));
            source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var queued = (Task)(Call(owner, "SendDataAsync", 1, null, new byte[] { 1, 2, 3 }, 0, 3, true,
                cancelApplication ? canceled.Token : remaining,
                cancelApplication ? remaining : canceled.Token)
                ?? throw new AssertionException("Missing queued DATA task."));
            var transport = ConnectionType.GetField("_transport", Hidden)?.GetValue(owner)
                ?? throw new AssertionException("Missing transport owner.");
            var queue = transport.GetType().GetField("_queue", Hidden)?.GetValue(transport) as IEnumerable
                ?? throw new AssertionException("Missing output queue.");
            var enumerator = queue.GetEnumerator();
            Assert.That(enumerator.MoveNext(), Is.True, "The DATA write must be queued behind the partial PING.");
            var write = new WeakReference(enumerator.Current);
            Assert.That(enumerator.MoveNext(), Is.False, "Only one DATA write belongs to this queue.");
            try
            {
                canceled.Cancel();
                try
                {
                    queued.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    Assert.Fail("The queued DATA write did not observe cancellation.");
                }
                catch (OperationCanceledException) { }
            }
            finally
            {
                source.Release.TrySetResult(true);
                first.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            // A leading write can complete before its queued siblings are drained.
            // An empty control barrier waits for the canceled entry to be removed
            // before Dispose can clean any entries that are still in the queue.
            var barrier = (Task)(Call(owner, "SendAsync", Array.CreateInstance(FrameType, 0), CancellationToken.None)
                ?? throw new AssertionException("Missing output barrier."));
            barrier.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Assert.That(source.ToArray(), Is.EqualTo(new byte[] { 0, 0, 8, 6, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
                "Only the complete PING may reach the wire; canceled DATA must remain uncommitted.");
            return (write, new WeakReference(owner));
        }

        private static object? Call(object owner, string name, params object?[] arguments)
            => (owner.GetType().GetMethod(name, Hidden) ?? throw new AssertionException("Missing method: " + name))
                .Invoke(owner, arguments);

        private static void Collect()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
            }
        }

        private sealed class GatedOutput : MemoryStream
        {
            private int _writes;
            internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Interlocked.Increment(ref _writes) == 1)
                {
                    base.Write(buffer, offset, 4);
                    Started.TrySetResult(true);
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    base.Write(buffer, offset + 4, count - 4);
                }
                else base.Write(buffer, offset, count);
            }
        }
    }
}
