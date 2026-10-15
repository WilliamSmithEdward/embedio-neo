using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http3OutputCancellationTest
    {
        [TestCase(false, "caller")]
        [TestCase(false, "exchange")]
        [TestCase(false, "both")]
        [TestCase(true, "caller")]
        [TestCase(true, "exchange")]
        [TestCase(true, "both")]
        public async Task CancellationPrecedesDisposedOutputAndPreservesItsToken(bool disposed, string cancellation)
        {
            using var caller = new CancellationTokenSource();
            using var lifetime = new CancellationTokenSource();
            using var fixture = new OutputFixture(lifetime.Token);
            if (cancellation != "exchange") caller.Cancel();
            if (cancellation != "caller") lifetime.Cancel();
            if (disposed) fixture.Dispose();
            Exception? failure = null;
            try
            {
                await fixture.Acquire(caller.Token);
                fixture.Release();
            }
            catch (OperationCanceledException error) { failure = error; }
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            var cancelled = failure as OperationCanceledException ?? throw new AssertionException("Missing cancellation.");
            Assert.That(cancelled.CancellationToken, Is.EqualTo(caller.IsCancellationRequested ? caller.Token : lifetime.Token));
            Assert.That(fixture.Users, Is.Zero, "Rejected work cannot retain an output user.");
        }

        [Test]
        public async Task DisposedUncancelledOutputStillRejectsUse()
        {
            using var fixture = new OutputFixture(CancellationToken.None);
            fixture.Dispose();
            Exception? failure = null;
            try { await fixture.Acquire(CancellationToken.None); }
            catch (ObjectDisposedException error) { failure = error; }
            Assert.That(failure, Is.InstanceOf<ObjectDisposedException>());
            Assert.That(fixture.Users, Is.Zero);
        }

        [Test]
        public async Task QueuedCallerCancellationReleasesItsUserBeforeLastOwnerDisposesGate()
        {
            using var caller = new CancellationTokenSource();
            using var fixture = new OutputFixture(CancellationToken.None);
            await fixture.Acquire(CancellationToken.None);
            var queued = fixture.Acquire(caller.Token);
            Assert.That(queued.IsCompleted, Is.False);
            Assert.That(fixture.Users, Is.EqualTo(2));
            fixture.Dispose();
            caller.Cancel();
            Exception? failure = null;
            try { await queued.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException error) { failure = error; }
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(fixture.Users, Is.EqualTo(1));
            fixture.Release();
            Assert.That(fixture.Users, Is.Zero);
            Assert.That(() => fixture.Gate.Wait(0), Throws.TypeOf<ObjectDisposedException>());
        }

        private sealed class OutputFixture : IDisposable
        {
            private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
            private readonly object _exchange;
            private readonly FieldInfo _users;
            private readonly Action _release;
            private readonly FieldInfo _gateField;
            internal SemaphoreSlim Gate => (SemaphoreSlim)(_gateField.GetValue(_exchange) ?? throw new AssertionException("Missing output gate."));
            internal readonly Func<CancellationToken, Task> Acquire;
            internal int Users => (int)(_users.GetValue(_exchange) ?? throw new AssertionException("Missing users."));

            internal OutputFixture(CancellationToken lifetime)
            {
                var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange");
                if (type == null)
                {
                    Assert.Ignore("The legacy assembly has no QUIC exchange.");
                    throw new AssertionException("Ignore did not terminate test.");
                }
                _exchange = RuntimeHelpers.GetUninitializedObject(type);
                _gateField = type.GetField("_output", Flags) ?? throw new AssertionException("Missing gate field.");
                Set(type, "_output", new SemaphoreSlim(1, 1));
                Set(type, "_outputLifetime", new object());
                Set(type, "<CancellationToken>k__BackingField", lifetime);
                var bodyType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3RequestBody", true)
                    ?? throw new AssertionException("Missing body type.");
                // The real body disposal path only marks it disposed. No transport is
                // accessed by this output-lifetime fixture, and no native QUIC is needed.
                Set(type, "<Body>k__BackingField", RuntimeHelpers.GetUninitializedObject(bodyType));
                _users = type.GetField("_outputUsers", Flags) ?? throw new AssertionException("Missing users field.");
                Acquire = (type.GetMethod("AcquireOutputAsync", Flags) ?? throw new AssertionException("Missing acquire.")).CreateDelegate<Func<CancellationToken, Task>>(_exchange);
                _release = (type.GetMethod("ReleaseOutput", Flags) ?? throw new AssertionException("Missing release.")).CreateDelegate<Action>(_exchange);
            }

            internal void Release() => _release();
            public void Dispose() => ((IDisposable)_exchange).Dispose();
            private void Set(Type type, string name, object value)
                => (type.GetField(name, Flags) ?? throw new AssertionException("Missing fixture field.")).SetValue(_exchange, value);
        }
    }
}
