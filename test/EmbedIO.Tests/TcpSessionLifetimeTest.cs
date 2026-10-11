using System;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TcpSessionLifetimeTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type OwnerType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.TcpSessionLifetime", true)
            ?? throw new AssertionException("Missing transport lifetime owner.");

        [TestCase(false)]
        [TestCase(true)]
        public async Task CleanupFailureDoesNotSkipOtherResourcesOrReportSuccessfulDrain(bool streamFails)
        {
            var failure = new IOException("controlled cleanup failure");
            var stream = new ObservedStream(streamFails ? failure : null);
            var input = new ObservedInput(streamFails ? null : failure);
            var owner = Create(stream);
            var completion = Completion(owner);
            var detached = 0;
            Assert.That(Retire(owner, false), Is.True);
            Assert.That(Assert.Throws<IOException>(() => Finish(owner, input, () => detached++)), Is.SameAs(failure));
            Assert.That(await Assert.ThrowsAsync<IOException>(async () => await completion), Is.SameAs(failure));
            Assert.Multiple(() =>
            {
                Assert.That(stream.Disposals, Is.EqualTo(1));
                Assert.That(input.Disposals, Is.EqualTo(1));
                Assert.That(detached, Is.EqualTo(1));
                Assert.That(Closed(owner), Is.True);
                Assert.That(Retire(owner, false), Is.False);
                Assert.That(TimerDisposed(Timer(owner)), Is.True);
            });
        }

        [Test]
        public async Task RacingRetirementHasOneClaimantAndDrainWaitsForProtocolDetach()
        {
            var stream = new ObservedStream(null);
            var owner = Create(stream);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var completion = Completion(owner);
            Assert.That(Retire(owner, false), Is.True);
            var closing = Task.Run(() => Finish(owner, null, () =>
            {
                entered.SetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Missing cleanup release.");
            }));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Multiple(() =>
                {
                    Assert.That(Retire(owner, false), Is.False);
                    Assert.That(completion.IsCompleted, Is.False);
                    Assert.That(Closed(owner), Is.False);
                    Assert.That(stream.Disposals, Is.EqualTo(1));
                });
            }
            finally { release.Set(); }
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Closed(owner), Is.True);
        }

        [Test]
        public void StaleDeadlineCannotRetireStoppedOrRenewedRequest()
        {
            var owner = Create(new ObservedStream(null));
            try
            {
                Method(owner, "ArmDeadline").CreateDelegate<Action<int>>(owner)(60000);
                Method(owner, "StopDeadline").CreateDelegate<Action>(owner)();
                Assert.That(Retire(owner, true), Is.False);
                Method(owner, "ArmDeadline").CreateDelegate<Action<int>>(owner)(60000);
                Assert.That(Retire(owner, true), Is.False);
                Assert.That(Closed(owner), Is.False);
            }
            finally
            {
                if (Retire(owner, false)) Finish(owner, null, () => { });
            }
            Assert.That(Completion(owner).IsCompletedSuccessfully, Is.True);
        }

        [Test]
        public async Task EarlyExpiryAttemptDoesNotLoseTheArmedDeadline()
        {
            object? owner = null;
            owner = Create(new ObservedStream(null), () =>
            {
                var current = owner ?? throw new AssertionException("Missing deadline owner.");
                if (Retire(current, true)) Finish(current, null, () => { });
            });
            try
            {
                Method(owner, "ArmDeadline").CreateDelegate<Action<int>>(owner)(1000);
                Assert.That(Retire(owner, true), Is.False);
                await Completion(owner).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(Closed(owner), Is.True);
            }
            finally { if (Retire(owner, false)) Finish(owner, null, () => { }); }
        }

        private static object Create(Stream stream, Action? expired = null)
            => Activator.CreateInstance(OwnerType, Hidden, null, new object[]
            {
                new object(), new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), stream, expired ?? (Action)(() => { }),
            }, null) ?? throw new AssertionException("Missing lifetime instance.");
        private static MethodInfo Method(object owner, string name)
            => owner.GetType().GetMethod(name, Hidden) ?? throw new AssertionException("Missing lifetime operation " + name);
        private static bool Retire(object owner, bool deadlineOnly)
            => Method(owner, "TryRetire").CreateDelegate<Func<bool, bool>>(owner)(deadlineOnly);
        private static void Finish(object owner, IDisposable? input, Action detach)
            => Method(owner, "FinishRetirement").CreateDelegate<Action<bool, IDisposable?, Action>>(owner)(true, input, detach);
        private static Task Completion(object owner)
            => OwnerType.GetProperty("Completion", Hidden)?.GetValue(owner) as Task ?? throw new AssertionException("Missing cleanup task.");
        private static bool Closed(object owner)
            => (bool)(OwnerType.GetProperty("CloseFinished", Hidden)?.GetValue(owner) ?? throw new AssertionException("Missing closed state."));
        private static Timer Timer(object owner)
            => OwnerType.GetProperty("DeadlineTimer", Hidden)?.GetValue(owner) as Timer ?? throw new AssertionException("Missing deadline timer.");
        private static bool TimerDisposed(Timer timer)
        {
            try { return !timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { return true; }
        }

        private sealed class ObservedStream(Exception? failure) : MemoryStream
        {
            internal int Disposals { get; private set; }
            protected override void Dispose(bool disposing)
            {
                if (disposing) Disposals++;
                base.Dispose(disposing);
                if (disposing && failure != null) throw failure;
            }
        }
        private sealed class ObservedInput(Exception? failure) : IDisposable
        {
            internal int Disposals { get; private set; }
            public void Dispose()
            {
                Disposals++;
                if (failure != null) throw failure;
            }
        }
    }
}
