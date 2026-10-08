using System;
using System.IO;
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
            internal Task<int> Reserve(int id, int max, CancellationToken token = default)
                => (Task<int>)(Call("ReserveAsync", id, max, token) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
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
