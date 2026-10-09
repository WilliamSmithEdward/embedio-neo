using System;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ManagedWebSocketCompletionTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type SocketType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket", true)
            ?? throw new AssertionException("Missing managed socket.");

        private static object Create(Action close)
        {
            var socket = Activator.CreateInstance(SocketType, Flags, null, new object[] { Stream.Null, close }, null)
                ?? throw new AssertionException("Missing socket constructor.");
            GC.SuppressFinalize(socket);
            return socket;
        }

        private static Task Observe(object socket, CancellationToken token = default)
            => (Task)(Invoke(socket, "WaitForCloseAsync", new object[] { token }) ?? throw new AssertionException("Missing close task."));

        private static object? Invoke(object socket, string name, object[]? args = null)
        {
            try
            {
                return (SocketType.GetMethod(name, Flags) ?? throw new AssertionException("Missing " + name)).Invoke(socket, args);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        [TestCase("abort")]
        [TestCase("close")]
        [TestCase("dispose")]
        public async Task ObserversWaitForCleanupAndLateObserversComplete(string ending)
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var calls = 0;
            var socket = Create(() =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cleanup gate was not released.");
            });
            var observer = Observe(socket);
            var close = Task.Run(async () =>
            {
                if (ending == "abort") Invoke(socket, "AbortSend");
                else if (ending == "dispose") ((IDisposable)socket).Dispose();
                else await ((EmbedIO.WebSockets.IWebSocket)socket).CloseAsync();
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(observer.IsCompleted, Is.False);
                Assert.That(Observe(socket).IsCompleted, Is.False);
            }
            finally { release.Set(); }
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            await observer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Observe(socket).IsCompletedSuccessfully, Is.True);
            Assert.That(((EmbedIO.WebSockets.IWebSocket)socket).State, Is.EqualTo(WebSocketState.Closed));
            ((IDisposable)socket).Dispose();
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ObserverCancellationDoesNotCancelOtherObservers(bool alreadyCanceled)
        {
            var calls = 0;
            var socket = Create(() => Interlocked.Increment(ref calls));
            using var stop = new CancellationTokenSource();
            if (alreadyCanceled) stop.Cancel();
            var canceled = Observe(socket, stop.Token);
            var healthy = Observe(socket);
            stop.Cancel();
            try
            {
                await canceled.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail("Observer cancellation was ignored.");
            }
            catch (OperationCanceledException error) { Assert.That(error.CancellationToken, Is.EqualTo(stop.Token)); }
            Assert.That(healthy.IsCompleted, Is.False);
            Assert.That(calls, Is.Zero);
            Invoke(socket, "AbortSend");
            await healthy.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(calls, Is.EqualTo(1));
        }

        // A protocol failure whose close frame cannot be written must still release the
        // connection once, and the write error must not fault the detached receive task.
        [Test]
        public async Task FailedCloseWriteAfterProtocolErrorIsContained()
        {
            var unobserved = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> observe = (_, e) =>
            {
                if (e.Exception.ToString().Contains("Injected close write failure.", StringComparison.Ordinal)) Interlocked.Increment(ref unobserved);
            };
            TaskScheduler.UnobservedTaskException += observe;
            try
            {
                for (var attempt = 0; attempt < 20; ++attempt)
                {
                    var calls = 0;
                    // An unmasked client frame fails the connection with 1002.
                    using var transport = new FailingWriteStream(new byte[] { 0x81, 0x01, 0x41 });
                    var socket = (SocketType.GetMethod("FromStream", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new AssertionException("Missing FromStream.")).Invoke(null, new object[] { transport, (Action)(() => Interlocked.Increment(ref calls)) })
                        ?? throw new AssertionException("Missing socket.");
                    await Observe(socket).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(((EmbedIO.WebSockets.IWebSocket)socket).State, Is.EqualTo(WebSocketState.Closed));
                    Assert.That(calls, Is.EqualTo(1));
                    ((IDisposable)socket).Dispose();
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.That(Volatile.Read(ref unobserved), Is.Zero);
            }
            finally { TaskScheduler.UnobservedTaskException -= observe; }
        }

        private sealed class FailingWriteStream : MemoryStream
        {
            internal FailingWriteStream(byte[] input) : base(input) { }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Task.FromException(new IOException("Injected close write failure."));
        }

        [Test]
        public async Task ThrowingTransportCloseStillCompletesObservers()
        {
            var socket = Create(() => throw new IOException("Injected transport close failure."));
            var observer = Observe(socket);
            Assert.That(() => Invoke(socket, "AbortSend"), Throws.InstanceOf<IOException>());
            await observer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Observe(socket).IsCompletedSuccessfully, Is.True);
            Assert.That((SocketType.GetField("_stream", Flags) ?? throw new AssertionException("Missing transport field.")).GetValue(socket), Is.Null);
        }
    }
}
