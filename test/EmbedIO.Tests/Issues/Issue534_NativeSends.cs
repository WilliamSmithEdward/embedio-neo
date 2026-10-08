using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue534_NativeSends
    {
        [TestCase(false)]
        [TestCase(true)]
        [Platform("Win")]
        public async Task NativeWindowsCloseOutputDoesNotOverlapASendAndCancellationDrainsItsUsers(bool cancelClose)
        {
            using var native = new ControlledSocket();
            using var adapter = Wrap(native);
            var sending = adapter.SendAsync(new byte[] { 1 }, false);
            await native.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource();
            var closing = adapter.CloseAsync(cancel.Token);
            Assert.That(native.OutputCount, Is.Zero);
            if (cancelClose)
            {
                cancel.Cancel();
                await Assert.ThatAsync(async () => await closing, Throws.InstanceOf<OperationCanceledException>());
                await Assert.ThatAsync(async () => await sending, Throws.TypeOf<ObjectDisposedException>());
                Assert.That(native.OutputCount, Is.Zero);
            }
            else
            {
                native.Release.TrySetResult(true);
                await Task.WhenAll(sending, closing).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(native.OutputCount, Is.EqualTo(1));
                Assert.That(adapter.State, Is.EqualTo(WebSocketState.Closed));
            }
            adapter.Dispose();
            Assert.That(((adapter).GetType().GetField("_gatesDisposed", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(adapter), Is.True);
        }

        [Test]
        public async Task NativeSendsSerializeAndRetainMessageTypeAndPayload()
        {
            using var native = new ControlledSocket();
            using var adapter = Wrap(native);
            var first = adapter.SendAsync(new byte[] { 1 }, false);
            await native.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = adapter.SendAsync(new byte[] { 2 }, true);
            native.Release.TrySetResult(true);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(native.MaximumSenders, Is.EqualTo(1));
            Assert.That(native.Messages.ToArray(), Is.EqualTo(new[] { (WebSocketMessageType.Binary, (byte)1), (WebSocketMessageType.Text, (byte)2) }));
        }

        [Test]
        public async Task NativeQueuedCancellationDoesNotSendOrReleaseAnUnacquiredPermit()
        {
            using var native = new ControlledSocket();
            using var adapter = Wrap(native);
            var first = adapter.SendAsync(new byte[] { 1 }, false);
            await native.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource();
            var second = adapter.SendAsync(new byte[] { 2 }, true, cancel.Token);
            cancel.Cancel();
            try
            {
                await Assert.ThatAsync(async () => await second, Throws.InstanceOf<OperationCanceledException>());
                Assert.That(native.Messages.Count, Is.EqualTo(1));
            }
            finally { native.Release.TrySetResult(true); }
            await first;
            await adapter.SendAsync(new byte[] { 3 }, false);
            Assert.That(native.MaximumSenders, Is.EqualTo(1));
            Assert.That(native.Messages.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task NativeDisposeDrainsActiveAndQueuedSendersBeforeDisposingTheirGates()
        {
            using var native = new ControlledSocket();
            var adapter = Wrap(native);
            var first = adapter.SendAsync(new byte[] { 1 }, false);
            await native.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = adapter.SendAsync(new byte[] { 2 }, true);
            adapter.Dispose();
            await Assert.ThatAsync(async () => await first, Throws.TypeOf<ObjectDisposedException>());
            await Assert.ThatAsync(async () => await second, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(((adapter).GetType().GetField("_gatesDisposed", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(adapter), Is.True);
            Assert.That(native.MaximumSenders, Is.EqualTo(1));
        }

        [Test]
        public async Task NativeFailureReleasesItsSendPermitAndNullValidationRemainsSynchronous()
        {
            using var native = new ControlledSocket { FailWrite = true };
            native.Release.TrySetResult(true);
            using var adapter = Wrap(native);
            Assert.Throws<ArgumentNullException>(() => TestObjects.InvalidInput.Invoke((Func<byte[], bool, CancellationToken, Task>)adapter.SendAsync, null, true, CancellationToken.None));
            await Assert.ThatAsync(async () => await adapter.SendAsync(new byte[] { 1 }, false), Throws.TypeOf<IOException>());
            native.FailWrite = false;
            await adapter.SendAsync(new byte[] { 2 }, true);
            Assert.That(native.MaximumSenders, Is.EqualTo(1));
        }

        private static IWebSocket Wrap(System.Net.WebSockets.WebSocket socket)
            => (IWebSocket)(Activator.CreateInstance((typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.SystemWebSocket", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), socket) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        private sealed class ControlledSocket : System.Net.WebSockets.WebSocket
        {
            private int _active;
            private int _state = (int)WebSocketState.Open;
            public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ConcurrentQueue<(WebSocketMessageType Type, byte Byte)> Messages { get; } = new();
            public int MaximumSenders;
            public int OutputCount;
            public bool FailWrite { get; set; }
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override string? SubProtocol => null;
            public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
            public override void Abort() => Dispose();
            public override void Dispose() { Volatile.Write(ref _state, (int)WebSocketState.Aborted); Release.TrySetResult(true); }
            public override Task CloseAsync(WebSocketCloseStatus status, string? reason, CancellationToken token) => throw new NotSupportedException();
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string? reason, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Assert.That(_active, Is.Zero, "Native close output is a send operation too.");
                Interlocked.Increment(ref OutputCount);
                Volatile.Write(ref _state, (int)WebSocketState.CloseSent);
                return Task.CompletedTask;
            }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Volatile.Write(ref _state, (int)WebSocketState.Closed);
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
            {
                if (State != WebSocketState.Open) throw new ObjectDisposedException(nameof(ControlledSocket));
                token.ThrowIfCancellationRequested();
                var active = Interlocked.Increment(ref _active);
                MaximumSenders = Math.Max(MaximumSenders, active);
                try
                {
                    Assert.That(end, Is.True);
                    Messages.Enqueue((type, (buffer.Array ?? throw new NUnit.Framework.AssertionException("Expected a send buffer."))[buffer.Offset]));
                    Started.TrySetResult(true);
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    if (State != WebSocketState.Open) throw new ObjectDisposedException(nameof(ControlledSocket));
                    if (FailWrite) throw new IOException("Deliberate native send failure.");
                }
                finally { Interlocked.Decrement(ref _active); }
            }
        }
    }
}
