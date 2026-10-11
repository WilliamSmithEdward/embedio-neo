using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2SendFlowControlTest
    {
        private sealed class Flow
        {
            private static readonly Type Type = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2SendFlowControl", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            private readonly object _instance = (Activator.CreateInstance(Type, true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            internal object? Call(string method, params object[] args)
            {
                try { return (Type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(_instance, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture((error.InnerException ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."))).Throw(); throw; }
            }
            internal void Open(int id) => Call("Open", id);
            internal void Update(int id, int count) => Call("Update", id, count);
            internal void Adjust(int delta) => Call("AdjustInitialWindow", delta);
            internal int Pending => (int)(Type.GetProperty("PendingCount")?.GetValue(_instance) ?? throw new AssertionException("Missing waiter count."));
            internal void Priority(int id, int urgency, bool incremental = false)
            {
                var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpPriority", true) ?? throw new AssertionException("Missing priority.");
                var value = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { urgency, incremental }, null)
                    ?? throw new AssertionException("Missing priority value.");
                Call("SetPriority", id, value);
            }
            internal Task<int> Reserve(int id, int max, CancellationToken token = default)
                => (Task<int>)(Call("ReserveAsync", id, max, token) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        }

        [Test]
        public async Task ScarceCreditGoesToMostUrgentWritableStream()
        {
            var flow = new Flow(); flow.Open(1); flow.Open(3); flow.Open(5);
            await flow.Reserve(5, 65535);
            flow.Priority(1, 7); flow.Priority(3, 0);
            var low = flow.Reserve(1, 1); var high = flow.Reserve(3, 1);
            flow.Update(0, 1);
            Assert.That(await high.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1));
            Assert.That(low.IsCompleted, Is.False);
            flow.Update(0, 1); Assert.That(await low, Is.EqualTo(1));
            Assert.That(flow.Pending, Is.Zero);
        }
        [Test]
        public async Task ReprioritizationChangesAlreadyWaitingReservations()
        {
            var flow = new Flow(); flow.Open(1); flow.Open(3); flow.Open(5);
            await flow.Reserve(5, 65535);
            flow.Priority(1, 0); flow.Priority(3, 7);
            var first = flow.Reserve(1, 1); var second = flow.Reserve(3, 1);
            flow.Priority(3, 0); flow.Priority(1, 7);
            flow.Update(0, 1);
            Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1));
            Assert.That(first.IsCompleted, Is.False);
            flow.Update(0, 1); await first;
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task EqualUrgencyUsesStreamOrderOrIncrementalQueueOrder(bool incremental)
        {
            var flow = new Flow(); flow.Open(1); flow.Open(3); flow.Open(5);
            await flow.Reserve(5, 65535);
            flow.Priority(1, 2, incremental); flow.Priority(3, 2, incremental);
            var laterStream = flow.Reserve(3, 1); var earlierStream = flow.Reserve(1, 1);
            flow.Update(0, 1);
            var winner = incremental ? laterStream : earlierStream;
            var loser = incremental ? earlierStream : laterStream;
            Assert.That(await winner.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1));
            Assert.That(loser.IsCompleted, Is.False);
            flow.Update(0, 1); await loser;
        }
        [Test]
        public async Task HighPriorityWithoutStreamCreditCannotBlockWritableLowerPriority()
        {
            var flow = new Flow(); flow.Adjust(-65535); flow.Open(1); flow.Open(3);
            flow.Priority(1, 0); flow.Priority(3, 7);
            var high = flow.Reserve(1, 1);
            flow.Update(3, 1);
            Assert.That(await flow.Reserve(3, 1).WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1));
            Assert.That(high.IsCompleted, Is.False);
            flow.Update(1, 1); await high;
        }
        [Test]
        public async Task CancellationGrantRacesCompleteWithoutLosingConnectionCredit()
        {
            for (var round = 0; round < 256; round++)
            {
                var flow = new Flow(); flow.Adjust(-65535); flow.Open(1); flow.Open(3);
                using var stop = new CancellationTokenSource();
                var reserved = Task.Run(async () => await flow.Reserve(1, 1, stop.Token));
                await Task.WhenAll(Task.Run(() => stop.Cancel()), Task.Run(() => flow.Update(1, 1))).WaitAsync(TimeSpan.FromSeconds(5));
                var granted = 0;
                try { granted = await reserved.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException error) { Assert.That(error.CancellationToken, Is.EqualTo(stop.Token)); }
                Assert.That(flow.Pending, Is.Zero);
                flow.Update(3, 65535);
                Assert.That(await flow.Reserve(3, 65535), Is.EqualTo(65535 - granted));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SeededPriorityChangesCancellationAndWindowChangesMatchOrderingModel(bool resetWindows)
        {
            var random = new Random(9218);
            for (var round = 0; round < 16; round++)
            {
                var flow = new Flow(); flow.Open(257);
                await flow.Reserve(257, 65535); flow.Call("Close", 257);
                using var cancel = new CancellationTokenSource();
                var ids = Enumerable.Range(0, 128).Select(index => index * 2 + 1).ToArray();
                random.Shuffle(ids);
                var urgency = new int[256]; var incremental = new bool[256]; var order = new int[256];
                var pending = new Dictionary<int, Task<int>>();
                for (var index = 0; index < ids.Length; index++)
                {
                    var id = ids[index]; order[id] = index;
                    urgency[id] = random.Next(8); incremental[id] = random.Next(2) == 0;
                    flow.Open(id); flow.Priority(id, urgency[id], incremental[id]);
                    pending.Add(id, flow.Reserve(id, 1, id % 5 == 1 ? cancel.Token : default));
                }
                if (resetWindows) flow.Adjust(-65535);
                foreach (var id in ids.Where(id => id % 3 == 1))
                {
                    urgency[id] = random.Next(8); incremental[id] = random.Next(2) == 0;
                    flow.Priority(id, urgency[id], incremental[id]);
                }
                cancel.Cancel();
                foreach (var id in ids.Where(id => id % 5 == 1))
                {
                    await Assert.ThatAsync(async () => await pending[id], Throws.InstanceOf<OperationCanceledException>());
                    pending.Remove(id);
                }
                foreach (var id in ids.Where(id => id % 5 != 1 && id % 7 == 1))
                {
                    flow.Call("Close", id);
                    await Assert.ThatAsync(async () => await pending[id], Throws.InstanceOf<IOException>());
                    pending.Remove(id);
                }
                if (resetWindows)
                    foreach (var id in pending.Keys) flow.Update(id, 1);
                var expected = pending.Keys.OrderBy(id => urgency[id]).ThenBy(id => incremental[id])
                    .ThenBy(id => incremental[id] ? order[id] : id).ToArray();
                foreach (var id in expected)
                {
                    flow.Update(0, 1);
                    Assert.That(await pending[id].WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1));
                    pending.Remove(id);
                    Assert.That(pending.Values.All(task => !task.IsCompleted), Is.True);
                    Assert.That(flow.Pending, Is.EqualTo(pending.Count));
                }
            }
        }

        [Test]
        public async Task ConnectionCreditIsSharedAndStreamCreditIsIndependent()
        {
            var flow = new Flow(); flow.Open(1); flow.Open(3);
            Assert.That(await flow.Reserve(1, 60000), Is.EqualTo(60000));
            Assert.That(await flow.Reserve(3, 10000), Is.EqualTo(5535));
            var blocked = flow.Reserve(3, 10000);
            Assert.That(blocked.IsCompleted, Is.False);
            flow.Update(1, 10000);
            Assert.That(blocked.IsCompleted, Is.False);
            flow.Update(0, 1234);
            Assert.That(await blocked.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(1234));
        }

        [Test]
        public async Task SettingsReductionCanLeaveNegativeStreamCredit()
        {
            var flow = new Flow(); flow.Open(1);
            Assert.That(await flow.Reserve(1, 100), Is.EqualTo(100));
            flow.Adjust(-65535);
            var blocked = flow.Reserve(1, 20);
            flow.Update(1, 100);
            Assert.That(blocked.IsCompleted, Is.False);
            flow.Update(1, 7);
            Assert.That(await blocked.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(7));
            flow.Open(3);
            var fresh = flow.Reserve(3, 20);
            Assert.That(fresh.IsCompleted, Is.False);
            flow.Adjust(11);
            Assert.That(await fresh.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(11));
        }

        [Test]
        public async Task CancelingOneWaiterDoesNotCancelOthersOrSpendCredit()
        {
            var flow = new Flow(); flow.Adjust(-65535); flow.Open(1); flow.Open(3);
            using var cancel = new CancellationTokenSource();
            var first = flow.Reserve(1, 10, cancel.Token);
            var second = flow.Reserve(3, 10);
            cancel.Cancel();
            await Assert.ThatAsync(async () => await first, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(second.IsCompleted, Is.False);
            flow.Update(3, 9);
            Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(9));
            flow.Update(1, 10);
            Assert.That(await flow.Reserve(1, 10), Is.EqualTo(10));
        }

        [Test]
        public async Task ClosingStreamAndConnectionReleaseBlockedWriters()
        {
            var flow = new Flow(); flow.Adjust(-65535); flow.Open(1); flow.Open(3);
            var first = flow.Reserve(1, 1); var second = flow.Reserve(3, 1);
            flow.Call("Close", 1);
            await Assert.ThatAsync(async () => await first.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<IOException>());
            Assert.That(second.IsCompleted, Is.False);
            flow.Call("Abort", new IOException("transport failed"));
            await Assert.ThatAsync(async () => await second.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<IOException>());
            Assert.Throws<IOException>(() => flow.Open(5));
        }

        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(0, 3)]
        [TestCase(1, 3)]
        public void WindowUpdateErrorsHaveCorrectScope(int id, int code)
        {
            var flow = new Flow(); flow.Open(1);
            var error = (Assert.Catch<IOException>(() => flow.Update(id, code == 1 ? 0 : int.MaxValue)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That((error.GetType().GetProperty("StreamId") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo(id));
            Assert.That((error.GetType().GetProperty("ErrorCode") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo((uint)code));
        }

        [Test]
        public async Task SettingsOverflowIsConnectionErrorAndDoesNotPartiallyMutate()
        {
            var flow = new Flow(); flow.Open(1); flow.Open(3);
            flow.Update(3, int.MaxValue - 65535);
            var error = (Assert.Catch<IOException>(() => flow.Adjust(1)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That((error.GetType().GetProperty("StreamId") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo(0));
            Assert.That((error.GetType().GetProperty("ErrorCode") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(error), Is.EqualTo(3u));
            flow.Update(0, 100);
            Assert.That(await flow.Reserve(1, 65536), Is.EqualTo(65535));
        }

        [Test]
        public async Task ConcurrentReservationsNeverOverspendConnectionCredit()
        {
            var flow = new Flow();
            for (var id = 1; id <= 64; id++) flow.Open(id);
            using var cancel = new CancellationTokenSource();
            var reservations = Enumerable.Range(1, 64).Select(id => Task.Run(async () => await flow.Reserve(id, 4096, cancel.Token))).ToArray();
            // Exactly sixteen positive reservations consume all 65535 bytes.
            var pending = reservations.ToList(); var total = 0;
            for (var i = 0; i < 16; i++)
            {
                var done = await Task.WhenAny(pending).WaitAsync(TimeSpan.FromSeconds(5));
                total += await done; pending.Remove(done);
            }
            Assert.That(total, Is.EqualTo(65535));
            cancel.Cancel();
            foreach (var task in pending)
                await Assert.ThatAsync(async () => await task, Throws.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public async Task PreCanceledReservationDoesNotConsumeCredit()
        {
            var flow = new Flow(); flow.Open(1);
            await Assert.ThatAsync(async () => await flow.Reserve(1, 65535, new CancellationToken(true)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(await flow.Reserve(1, 65535), Is.EqualTo(65535));
        }
    }
}
