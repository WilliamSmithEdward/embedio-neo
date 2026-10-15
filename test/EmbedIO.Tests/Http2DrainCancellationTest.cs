using System;
using System.Collections;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class Http2DrainCancellationTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationDuringTheFinalDrainBarrierPreservesTheActualOutcome(bool outputFailure)
        {
            var core = typeof(WebServer).Assembly;
            var connectionType = core.GetType("EmbedIO.Net.Internal.Http2.Http2Connection")
                ?? throw new AssertionException("Missing HTTP/2 connection.");
            var dispatcherType = core.GetType("EmbedIO.Net.Internal.Http2.Http2Dispatcher")
                ?? throw new AssertionException("Missing HTTP/2 dispatcher.");
            var exchangeType = core.GetType("EmbedIO.Net.Internal.Http2.Http2Exchange")
                ?? throw new AssertionException("Missing HTTP/2 exchange.");
            using var stream = new MemoryStream();
            using var connection = (IDisposable)(Activator.CreateInstance(connectionType, Flags, null, new object[] { stream }, null)
                ?? throw new AssertionException("Missing connection instance."));
            using var dispatcher = (IDisposable)(Activator.CreateInstance(dispatcherType, Flags, null, new object[] { connection }, null)
                ?? throw new AssertionException("Missing dispatcher instance."));
            using var stop = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var credits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Enter the existing graceful-finalization path with its credit pump
            // held, then queue the real output barrier behind an occupied flusher.
            Field(dispatcher, "_gracefulInputEnd").SetValue(dispatcher, true);
            Value<CancellationTokenSource>(dispatcher, "_inputStop").Cancel();
            Field(dispatcher, "_creditPump").SetValue(dispatcher, credits.Task);
            var transport = Value<object>(connection, "_transport");
            Field(transport, "_flushing").SetValue(transport, true);
            var outputSync = Value<object>(transport, "_outputSync");
            var queue = Value<ICollection>(transport, "_queue");
            var callback = Expression.Lambda(typeof(Func<,>).MakeGenericType(exchangeType, typeof(Task)),
                Expression.Constant(Task.CompletedTask, typeof(Task)), Expression.Parameter(exchangeType)).Compile();
            var runMethod = dispatcherType.GetMethod("RunAsync", Flags) ?? throw new AssertionException("Missing RunAsync.");
            var running = (Task)(runMethod.Invoke(dispatcher, new object[] { callback, stop.Token })
                ?? throw new AssertionException("Missing dispatcher task."));
            try
            {
                Assert.That(running.IsCompleted, Is.False);
                credits.TrySetResult();
                object? barrier = null;
                while (barrier == null)
                {
                    lock (outputSync)
                    {
                        if (queue.Count != 0)
                        {
                            var iterator = queue.GetEnumerator();
                            Assert.That(iterator.MoveNext(), Is.True);
                            barrier = iterator.Current;
                        }
                    }
                    if (barrier == null) await Task.Delay(1, deadline.Token);
                }
                Assert.That(Value<int>(barrier, "EstimatedBytes", property: true), Is.Zero,
                    "The held write must be the final empty barrier, not application DATA.");
                if (outputFailure)
                    Value<TaskCompletionSource<bool>>(barrier, "Completion").TrySetException(new IOException("Controlled output failure."));
                stop.Cancel();
                if (outputFailure)
                {
                    var error = await Assert.ThrowsAsync<IOException>(async () => await running.WaitAsync(deadline.Token));
                    Assert.That(error?.Message, Is.EqualTo("Controlled output failure."));
                }
                else await running.WaitAsync(deadline.Token);
            }
            finally
            {
                credits.TrySetResult();
                stop.Cancel();
                // The assertion above observes the outcome. Never leave the held
                // task alive when a fixture assertion or timeout fails.
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) when (outputFailure) { }
            }
        }

        private static FieldInfo Field(object instance, string name)
        {
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Flags);
                if (field != null) return field;
            }
            throw new AssertionException("Missing fixture field: " + name);
        }

        private static T Value<T>(object instance, string name, bool property = false)
            => (T)((property ? instance.GetType().GetProperty(name, Flags)?.GetValue(instance) : Field(instance, name).GetValue(instance))
                ?? throw new AssertionException("Missing fixture value: " + name));
    }
}
