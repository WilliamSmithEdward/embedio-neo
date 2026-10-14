using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Behavior the request hot path keeps while it allocates and locks less.
    [NonParallelizable]
    public class HotPathRegressionTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly AsyncLocal<string?> Ambient = new();

        [Test]
        public async Task WriteGateEntersUncontendedSynchronouslyAndKeepsCancellationAndRetirement()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.AsyncWriteGate", true) ?? throw new TypeLoadException("AsyncWriteGate");
            var enter = type.GetMethod("EnterAsync", Hidden) ?? throw new MissingMethodException("EnterAsync");
            var gate = (IDisposable)(Activator.CreateInstance(type, true) ?? throw new InvalidOperationException("Gate not created."));
            object Enter(CancellationToken token) => enter.Invoke(gate, new object[] { token }) ?? throw new InvalidOperationException("No entry.");

            var first = Enter(CancellationToken.None);
            Assert.That(Get<bool>(first, "IsCompletedSuccessfully"), Is.True, "An uncontended writer must not wait asynchronously.");
            ((IDisposable)Get<object>(first, "Result")).Dispose();

            // A canceled token still wins over a free gate, and leaves the gate free.
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Assert.ThatAsync(() => AsTask(Enter(canceled.Token)), Throws.InstanceOf<OperationCanceledException>());
            }
            var second = Enter(CancellationToken.None);
            Assert.That(Get<bool>(second, "IsCompletedSuccessfully"), Is.True);
            ((IDisposable)Get<object>(second, "Result")).Dispose();

            // Retirement faults the returned entry instead of throwing from the call.
            gate.Dispose();
            object? retired = null;
            Assert.DoesNotThrow(() => retired = Enter(CancellationToken.None));
            await Assert.ThatAsync(() => AsTask(retired ?? throw new InvalidOperationException("No entry.")), Throws.TypeOf<ObjectDisposedException>());
        }

        [Test]
        public async Task RequestMessagesReachObserversWhileTracingIsOff()
        {
            var log = typeof(EmbedIO.Diagnostics.Log);
            var observerEvent = log.GetEvent("MessageWritten", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMemberException("MessageWritten");
            var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<string> observer = message =>
            {
                if (message.Contains("GET /observed -", StringComparison.Ordinal)) started.TrySetResult(message);
                if (message.Contains("GET /observed: \"200 ", StringComparison.Ordinal)) completed.TrySetResult(message);
            };
            var previous = EmbedIO.Diagnostics.Log.Source.Switch.Level;
            (observerEvent.GetAddMethod(true) ?? throw new MissingMethodException("add_MessageWritten")).Invoke(null, new object[] { observer });
            try
            {
                EmbedIO.Diagnostics.Log.Source.Switch.Level = SourceLevels.Off;
                using var server = new TestWebServer().OnAny(context => context.SendStringAsync("ok", "text/plain", Encoding.UTF8));
                server.Start();
                Assert.That(await server.Client.GetStringAsync("/observed"), Is.EqualTo("ok"));
                // Security criteria watch these messages; skipping them when nothing traces
                // must not starve observers of either the request or its completion.
                Assert.That(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)), Does.Contain("GET /observed - "));
                Assert.That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Does.Contain(" sent in "));
            }
            finally
            {
                (observerEvent.GetRemoveMethod(true) ?? throw new MissingMethodException("remove_MessageWritten")).Invoke(null, new object[] { observer });
                EmbedIO.Diagnostics.Log.Source.Switch.Level = previous;
            }
        }

        [Test]
        public async Task HandlersRunInTheExecutionContextThatStartedTheServer()
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(HttpListenerMode.EmbedIO, url);
            server.WithModule(new ActionModule("/", HttpVerbs.Any, context => context.SendStringAsync(Ambient.Value ?? "<none>", "text/plain", Encoding.UTF8)));
            using var stop = new CancellationTokenSource();
            Task running;
            Ambient.Value = "flowed";
            try { running = server.RunAsync(stop.Token); }
            finally { Ambient.Value = null; }
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("flowed"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [Test]
        public async Task RequestCompletionDoesNotCreateUnusedItems()
        {
            var url = Resources.GetServerAddress();
            var closed = new TaskCompletionSource<IHttpContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(HttpListenerMode.EmbedIO, url);
            server.WithModule(new ActionModule("/", HttpVerbs.Any, context =>
            {
                // Close runs after request completion, which looks for completion callbacks.
                context.OnClose(closedContext => closed.TrySetResult(closedContext));
                return context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("ok"));
                var context = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var items = context.GetType().GetField("_items", Hidden) ?? throw new MissingFieldException("_items");
                Assert.That(items.GetValue(context), Is.Null);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static T Get<T>(object target, string property)
            => (T)(target.GetType().GetProperty(property)?.GetValue(target) ?? throw new MissingMemberException(property));

        private static Task AsTask(object entry)
            => (Task)(entry.GetType().GetMethod("AsTask", Type.EmptyTypes)?.Invoke(entry, null) ?? throw new MissingMethodException("AsTask"));
    }
}
