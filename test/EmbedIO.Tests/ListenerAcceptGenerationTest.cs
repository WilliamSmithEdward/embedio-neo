using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ListenerAcceptGenerationTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        // No dispatcher is installed on these listeners, so every registration takes the
        // queue path that GetContextAsync serves; the returned handler task is always null.
        private static void Register(Net.HttpListener listener, IHttpContextImpl context)
            => _ = (typeof(Net.HttpListener).GetMethod("RegisterContext", Hidden) ?? throw new AssertionException("Missing context registration."))
                .CreateDelegate<Func<IHttpContextImpl, Task?>>(listener)(context);
        private static void Withdraw(Net.HttpListener listener, IHttpContextImpl context)
            => (typeof(Net.HttpListener).GetMethod("UnregisterContext", Hidden) ?? throw new AssertionException("Missing context withdrawal."))
                .CreateDelegate<Action<IHttpContextImpl>>(listener)(context);

        [Test]
        public async Task AcceptBeforeFirstStartReceivesItsFirstGeneration()
        {
            using var listener = new Net.HttpListener();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var accepting = listener.GetContextAsync(deadline.Token);
            Assert.That(accepting.IsCompleted, Is.False);
            listener.Start();
            var context = ListenerAdmissionConcurrencyTest.FakeContext.Create("initial");
            Register(listener, context);
            Assert.That(await accepting.WaitAsync(deadline.Token), Is.SameAs(context));
        }

        [Test]
        public async Task StoppedAcceptPreservesAlreadyCanceledCallerToken()
        {
            using var listener = new Net.HttpListener();
            listener.Start();
            listener.Stop();
            using var caller = new CancellationTokenSource();
            caller.Cancel();
            var accepting = listener.GetContextAsync(caller.Token);
            Assert.That(accepting.IsCanceled, Is.True);
            var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await accepting);
            Assert.That(error?.CancellationToken, Is.EqualTo(caller.Token));
        }

        [Test]
        public async Task StaleWithdrawalCannotRemoveAnotherContextWithTheSameIdentifier()
        {
            using var listener = new Net.HttpListener();
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var old = ListenerAdmissionConcurrencyTest.FakeContext.Create("reused");
            Register(listener, old);
            Withdraw(listener, old);
            var current = ListenerAdmissionConcurrencyTest.FakeContext.Create("reused");
            Register(listener, current);
            Withdraw(listener, old);
            Assert.That(await listener.GetContextAsync(deadline.Token), Is.SameAs(current));
        }

        [Test]
        public async Task RetiredWaiterCannotClaimAContextFromTheRestartedGeneration()
        {
            using var listener = new Net.HttpListener();
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = DispatchProxy.Create<IHttpContextImpl, ListenerAdmissionConcurrencyTest.PausedClaimContext>();
            var paused = (ListenerAdmissionConcurrencyTest.PausedClaimContext)(object)first;
            var oldAccept = listener.GetContextAsync(deadline.Token);
            try
            {
                Register(listener, first);
                await paused.Claiming.Task.WaitAsync(deadline.Token);
                listener.Stop();
                listener.Start();
                var current = ListenerAdmissionConcurrencyTest.FakeContext.Create("new-generation");
                Register(listener, current);
                paused.Resume.TrySetResult();
                var error = await Assert.ThrowsAsync<HttpListenerException>(async () => await oldAccept.WaitAsync(deadline.Token));
                Assert.That(error?.ErrorCode, Is.EqualTo(995));
                Assert.That(await listener.GetContextAsync(deadline.Token), Is.SameAs(current));
            }
            finally { paused.Resume.TrySetResult(); }
        }
    }
}
