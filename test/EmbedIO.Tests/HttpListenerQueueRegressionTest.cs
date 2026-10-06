using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpListenerQueueRegressionTest
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type ContextType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpListenerContext", true)!;

        [TestCase(1)]
        [TestCase(16)]
        [TestCase(256)]
        public async Task ConcurrentAcceptsDrainPrequeuedContextsExactlyOnce(int size)
        {
            using var listener = new Net.HttpListener();
            listener.Start(); // No endpoint is needed to exercise the queue lifecycle.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var queue = Queue(listener);
            try
            {
                var contexts = Enumerable.Range(0, size).Select(index => Context(index.ToString())).ToArray();
                foreach (var context in contexts) Register(listener, context);
                var accepts = contexts.Select(_ => Task.Run(() => listener.GetContextAsync(timeout.Token))).ToArray();
                var accepted = await Task.WhenAll(accepts).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(accepted.Select(context => context.Id), Is.EquivalentTo(contexts.Select(context => context.Id)));
                Assert.That(queue.Count, Is.Zero);
            }
            finally { timeout.Cancel(); queue.Clear(); }
        }

        [Test]
        public async Task UnregisteredContextsDoNotPreventAcceptingALaterRequest()
        {
            using var listener = new Net.HttpListener();
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var queue = Queue(listener);
            try
            {
                var stale = Context("disconnected");
                Register(listener, stale);
                typeof(Net.HttpListener).GetMethod("UnregisterContext", PrivateInstance)!.Invoke(listener, new object[] { stale });
                var accept = listener.GetContextAsync(timeout.Token);
                Assert.That(accept.IsCompleted, Is.False);
                var live = Context("live");
                Register(listener, live);
                Assert.That(await accept.WaitAsync(TimeSpan.FromSeconds(5)), Is.SameAs(live));
                Assert.That(queue.Count, Is.Zero);
            }
            finally { timeout.Cancel(); queue.Clear(); }
        }

        private static IHttpContextImpl Context(string id)
        {
            var context = RuntimeHelpers.GetUninitializedObject(ContextType);
            ContextType.GetField("<Id>k__BackingField", PrivateInstance)!.SetValue(context, id);
            return (IHttpContextImpl)context;
        }

        private static IDictionary Queue(Net.HttpListener listener)
            => (IDictionary)typeof(Net.HttpListener).GetField("_ctxQueue", PrivateInstance)!.GetValue(listener)!;

        private static void Register(Net.HttpListener listener, IHttpContextImpl context)
            => typeof(Net.HttpListener).GetMethod("RegisterContext", PrivateInstance)!.Invoke(listener, new object[] { context });
    }
}
