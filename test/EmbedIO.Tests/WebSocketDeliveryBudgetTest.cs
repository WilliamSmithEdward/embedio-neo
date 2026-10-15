using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class WebSocketDeliveryBudgetTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object Invoke(object instance, string method, params object?[] arguments)
        {
            try
            {
                return (instance.GetType().GetMethod(method, Flags) ?? throw new AssertionException("Missing budget method."))
                    .Invoke(instance, arguments) ?? new object();
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static object Create(string name, params object?[] arguments)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal." + name)
                ?? throw new AssertionException("Missing delivery budget type.");
            try
            {
                return Activator.CreateInstance(type, Flags, null, arguments, null)
                    ?? throw new AssertionException("Missing delivery budget constructor.");
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private sealed class Budget
        {
            internal readonly object Instance;
            internal Budget(int? connectionMessages = null, long? connectionBytes = null, int? moduleMessages = null, long? moduleBytes = null)
                => Instance = Create("WebSocketDeliveryBudget", Create("WebSocketDeliveryLimits", connectionMessages, connectionBytes, moduleMessages, moduleBytes));
            internal Connection Connect() => new(Invoke(Instance, "CreateConnection"));
        }

        private sealed class Connection
        {
            internal readonly object Instance;
            internal Connection(object instance) => Instance = instance;
            internal Lease? Reserve()
            {
                var arguments = new object?[] { null };
                var accepted = (bool)Invoke(Instance, "TryReserve", arguments);
                return accepted ? new Lease(arguments[0] ?? throw new AssertionException("Missing admitted lease.")) : null;
            }
            internal void Stop() => Invoke(Instance, "StopAdmission");
        }

        private sealed class Lease : IDisposable
        {
            private readonly object _instance;
            internal Lease(object instance) => _instance = instance;
            internal bool Grow(long bytes) => (bool)Invoke(_instance, "TryGrow", bytes);
            internal void Queue() => Invoke(_instance, "CompleteMessage");
            internal Task Deliver(Func<Task> callback) => (Task)Invoke(_instance, "DeliverAsync", callback);
            public void Dispose() => ((IDisposable)_instance).Dispose();
        }

        private static Lease Reserve(Connection connection)
            => connection.Reserve() ?? throw new AssertionException("Expected delivery admission.");

        private static void Rejected(Connection connection)
        {
            using var unexpected = connection.Reserve();
            Assert.That(unexpected, Is.Null);
        }

        private static void Outstanding(object owner, long messages, long bytes)
        {
            var snapshot = owner.GetType().GetProperty("Outstanding", Flags)?.GetValue(owner)
                ?? throw new AssertionException("Missing delivery snapshot.");
            Assert.That(snapshot.GetType().GetProperty("Messages", Flags)?.GetValue(snapshot), Is.EqualTo(messages));
            Assert.That(snapshot.GetType().GetProperty("Bytes", Flags)?.GetValue(snapshot), Is.EqualTo(bytes));
        }

        [TestCase(0, 0L, 1, 0L)]
        [TestCase(-1, 0L, 1, 0L)]
        [TestCase(1, -1L, 1, 0L)]
        [TestCase(1, 0L, 0, 0L)]
        [TestCase(1, 0L, -1, 0L)]
        [TestCase(1, 0L, 1, -1L)]
        public void InvalidLimitsAreRejected(int connectionMessages, long connectionBytes, int moduleMessages, long moduleBytes)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new Budget(connectionMessages, connectionBytes, moduleMessages, moduleBytes));

        [Test]
        public void EmptyMessagesConsumeCountWithZeroByteAllowance()
        {
            var budget = new Budget(1, 0, 2, 0);
            var first = budget.Connect();
            var second = budget.Connect();
            using var a = Reserve(first);
            using var b = Reserve(second);
            Assert.That(a.Grow(0), Is.True);
            Assert.That(a.Grow(1), Is.False);
            Rejected(first);
            Rejected(budget.Connect());
            Outstanding(budget.Instance, 2, 0);
        }

        [Test]
        public void ConnectionBytesRejectGrowthWithoutChangingModuleLedger()
        {
            var budget = new Budget(null, 5, null, 20);
            var connection = budget.Connect();
            using var a = Reserve(connection);
            using var b = Reserve(connection);
            Assert.That(a.Grow(3), Is.True);
            Assert.That(b.Grow(2), Is.True);
            Assert.That(a.Grow(1), Is.False);
            Outstanding(connection.Instance, 2, 5);
            Outstanding(budget.Instance, 2, 5);
        }

        [Test]
        public void ModuleBytesAreSharedByDifferentConnections()
        {
            var budget = new Budget(null, 10, null, 5);
            var first = budget.Connect();
            var second = budget.Connect();
            using var a = Reserve(first);
            using var b = Reserve(second);
            Assert.That(a.Grow(4), Is.True);
            Assert.That(b.Grow(2), Is.False);
            Outstanding(second.Instance, 1, 0);
            Assert.That(b.Grow(1), Is.True);
            Outstanding(budget.Instance, 2, 5);
        }

        [Test]
        public void UnlimitedByteAccountingRefusesOverflowAtomically()
        {
            var budget = new Budget();
            using var a = Reserve(budget.Connect());
            using var b = Reserve(budget.Connect());
            Assert.That(a.Grow(long.MaxValue), Is.True);
            Assert.That(a.Grow(1), Is.False);
            Assert.That(b.Grow(1), Is.False);
            Outstanding(budget.Instance, 2, long.MaxValue);
            a.Dispose();
            Assert.That(b.Grow(1), Is.True);
            Outstanding(budget.Instance, 1, 1);
        }

        [Test]
        public void FailedFragmentAdmissionPrecedesCallerBufferGrowth()
        {
            var budget = new Budget(null, 4);
            using var lease = Reserve(budget.Connect());
            var bytes = new byte[3];
            Assert.That(lease.Grow(bytes.Length), Is.True);
            if (lease.Grow(2)) Array.Resize(ref bytes, 5);
            Assert.That(bytes, Has.Length.EqualTo(3));
            Outstanding(budget.Instance, 1, 3);
        }

        [Test]
        public void NegativeGrowthAndGrowthAfterQueueAreRejected()
        {
            var budget = new Budget();
            using var lease = Reserve(budget.Connect());
            Assert.Throws<ArgumentOutOfRangeException>(() => lease.Grow(-1));
            lease.Queue();
            Assert.Throws<InvalidOperationException>(() => lease.Grow(0));
            Assert.Throws<InvalidOperationException>(lease.Queue);
            Outstanding(budget.Instance, 1, 0);
        }

        [Test]
        public void UndeliveredLeaseReleaseIsIdempotentAndReturnsAllBytes()
        {
            var budget = new Budget(1, 8, 1, 8);
            var connection = budget.Connect();
            using var lease = Reserve(connection);
            Assert.That(lease.Grow(8), Is.True);
            lease.Queue();
            lease.Dispose();
            lease.Dispose();
            Outstanding(connection.Instance, 0, 0);
            Outstanding(budget.Instance, 0, 0);
            using var replacement = Reserve(connection);
            Assert.That(replacement.Grow(8), Is.True);
        }

        [Test]
        public async Task StopAndDisposeDoNotReturnCallbackOwnedBytes()
        {
            var budget = new Budget(1, 8, 1, 8);
            var connection = budget.Connect();
            using var lease = Reserve(connection);
            Assert.That(lease.Grow(8), Is.True);
            lease.Queue();
            var callback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = lease.Deliver(() => callback.Task);
            connection.Stop();
            lease.Dispose();
            Outstanding(budget.Instance, 1, 8);
            Rejected(connection);
            Rejected(budget.Connect());
            Assert.That(delivered.IsCompleted, Is.False);
            callback.SetResult(true);
            await delivered.WaitAsync(TimeSpan.FromSeconds(5));
            Outstanding(budget.Instance, 0, 0);
            using var replacement = Reserve(budget.Connect());
        }

        [Test]
        public async Task QueuedMessageCanTransferAfterAdmissionStops()
        {
            var budget = new Budget();
            var connection = budget.Connect();
            using var lease = Reserve(connection);
            lease.Queue();
            connection.Stop();
            var calls = 0;
            await lease.Deliver(() => { calls++; return Task.CompletedTask; });
            Assert.That(calls, Is.EqualTo(1));
            Outstanding(budget.Instance, 0, 0);
        }

        [Test]
        public async Task LegacyCallbacksOverlapAndCanSettleOutOfOrder()
        {
            var budget = new Budget(2, 10, 2, 10);
            var connection = budget.Connect();
            using var a = Reserve(connection);
            using var b = Reserve(connection);
            Assert.That(a.Grow(3), Is.True);
            Assert.That(b.Grow(7), Is.True);
            a.Queue(); b.Queue();
            var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var starts = 0;
            var deliveryA = a.Deliver(() => { starts++; return first.Task; });
            var deliveryB = b.Deliver(() => { starts++; return second.Task; });
            Assert.That(starts, Is.EqualTo(2));
            second.SetResult(true);
            await deliveryB.WaitAsync(TimeSpan.FromSeconds(5));
            Outstanding(budget.Instance, 1, 3);
            Assert.That(deliveryA.IsCompleted, Is.False);
            first.SetResult(true);
            await deliveryA.WaitAsync(TimeSpan.FromSeconds(5));
            Outstanding(budget.Instance, 0, 0);
        }

        [Test]
        public async Task ReconnectSharesModuleBudgetWithOldCallback()
        {
            var budget = new Budget(2, 8, 2, 8);
            var old = budget.Connect();
            using var a = Reserve(old);
            Assert.That(a.Grow(6), Is.True);
            a.Queue();
            var callback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = a.Deliver(() => callback.Task);
            old.Stop();
            var current = budget.Connect();
            using var b = Reserve(current);
            Assert.That(b.Grow(3), Is.False);
            Assert.That(b.Grow(2), Is.True);
            Rejected(current);
            callback.SetResult(true);
            await delivered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(b.Grow(4), Is.True);
            Outstanding(budget.Instance, 1, 6);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CallbackFailureAndCancellationReturnLeaseAfterSettlement(bool cancelled)
        {
            var budget = new Budget();
            using var lease = Reserve(budget.Connect());
            Assert.That(lease.Grow(9), Is.True);
            lease.Queue();
            var callback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = lease.Deliver(() => callback.Task);
            lease.Dispose();
            Outstanding(budget.Instance, 1, 9);
            if (cancelled) callback.SetCanceled();
            else callback.SetException(new InvalidOperationException("owned callback marker"));
            if (cancelled) await Assert.ThatAsync(() => delivered, Throws.InstanceOf<OperationCanceledException>());
            else await Assert.ThatAsync(() => delivered, Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("owned callback marker"));
            Outstanding(budget.Instance, 0, 0);
        }

        [Test]
        public async Task SynchronousCallbackFailureReturnsLease()
        {
            var budget = new Budget();
            using var lease = Reserve(budget.Connect());
            lease.Queue();
            await Assert.ThatAsync(() => lease.Deliver(() => throw new InvalidOperationException("synchronous marker")),
                Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("synchronous marker"));
            Outstanding(budget.Instance, 0, 0);
        }

        [Test]
        public async Task DeliveryCannotStartTwiceOrBeforeQueue()
        {
            var budget = new Budget();
            using var lease = Reserve(budget.Connect());
            await Assert.ThatAsync(() => lease.Deliver(() => Task.CompletedTask), Throws.TypeOf<InvalidOperationException>());
            lease.Queue();
            await lease.Deliver(() => Task.CompletedTask);
            await Assert.ThatAsync(() => lease.Deliver(() => Task.CompletedTask), Throws.TypeOf<InvalidOperationException>());
            Outstanding(budget.Instance, 0, 0);
        }

        [Test]
        public async Task CallbackCanReserveFromAnotherThreadWithoutHoldingBudgetGate()
        {
            var budget = new Budget();
            var connection = budget.Connect();
            using var lease = Reserve(connection);
            lease.Queue();
            await lease.Deliver(() =>
            {
                // A synchronous callback must be able to join work using the
                // same budget on another thread. An async callback's first await
                // would yield and fail to detect a gate held only at invocation.
                using var sibling = Task.Run(() => Reserve(connection)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Assert.That(sibling.Grow(4), Is.True);
                Outstanding(budget.Instance, 2, 4);
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Outstanding(budget.Instance, 0, 0);
        }

        [Test]
        public async Task ConcurrentAdmissionNeverExceedsConnectionOrModuleLimits()
        {
            var budget = new Budget(2, 10, 5, 25);
            var connections = Enumerable.Range(0, 8).Select(_ => budget.Connect()).ToArray();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 64).Select(async index =>
            {
                await start.Task;
                var lease = connections[index % connections.Length].Reserve();
                if (lease != null) Assert.That(lease.Grow(5), Is.True);
                return lease;
            }).ToArray();
            start.SetResult(true);
            var admitted = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(admitted.Count(value => value != null), Is.EqualTo(5));
            Outstanding(budget.Instance, 5, 25);
            foreach (var connection in connections)
            {
                using var extra = connection.Reserve();
                Assert.That(extra, Is.Null);
            }
            await Task.WhenAll(admitted.Where(value => value != null).Select(value => Task.Run(() => value?.Dispose())));
            Outstanding(budget.Instance, 0, 0);
            foreach (var connection in connections) Outstanding(connection.Instance, 0, 0);
        }

        [Test]
        public async Task ConcurrentSameConnectionReservationsRespectItsCount()
        {
            var budget = new Budget(2, null, 20);
            var connection = budget.Connect();
            var leases = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(connection.Reserve)));
            Assert.That(leases.Count(value => value != null), Is.EqualTo(2));
            Outstanding(connection.Instance, 2, 0);
            foreach (var lease in leases) lease?.Dispose();
            Outstanding(budget.Instance, 0, 0);
        }

        [Test]
        public async Task ConcurrentFragmentGrowthIsAtomicAcrossBothByteBudgets()
        {
            var budget = new Budget(null, 3, null, 5);
            var first = budget.Connect();
            var second = budget.Connect();
            using var a = Reserve(first);
            using var b = Reserve(second);
            var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() => (index % 2 == 0 ? a : b).Grow(1))));
            Assert.That(results.Count(value => value), Is.EqualTo(5));
            Outstanding(budget.Instance, 2, 5);
            Assert.That(a.Grow(1), Is.False);
            Assert.That(b.Grow(1), Is.False);
            a.Dispose(); b.Dispose();
            Outstanding(first.Instance, 0, 0);
            Outstanding(second.Instance, 0, 0);
        }

        [Test]
        public async Task ConcurrentUndeliveredDisposalReleasesOnce()
        {
            var budget = new Budget(1, 4, 1, 4);
            var connection = budget.Connect();
            using var lease = Reserve(connection);
            Assert.That(lease.Grow(4), Is.True);
            lease.Queue();
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(lease.Dispose)));
            Outstanding(connection.Instance, 0, 0);
            Outstanding(budget.Instance, 0, 0);
            using var next = Reserve(connection);
            Assert.That(next.Grow(4), Is.True);
        }
    }
}
