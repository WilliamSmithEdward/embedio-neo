using System;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue502_CloseAdmission
    {
        [Test]
        public async Task CancellingASecondaryUnixCloseDoesNotIntroduceAnAbortOfTheExistingClose()
        {
            if (OperatingSystem.IsWindows()) Assert.Ignore("Unix native full-close delegation");
            using var native = new HeldSocket();
            using var socket = Wrap(native);
            var first = socket.CloseAsync();
            using var cancel = new CancellationTokenSource();
            var second = socket.CloseAsync(cancel.Token);
            cancel.Cancel();
            try
            {
                await Assert.ThatAsync(async () => await second, Throws.InstanceOf<OperationCanceledException>());
                Assert.That(native.AbortCount, Is.Zero);
            }
            finally { native.ReleaseSend.TrySetResult(true); }
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task CancelledValidCloseAbortsInsteadOfLeavingAnOpenConnectionThatRejectsDataForever()
        {
            using var native = new HeldSocket();
            using var socket = Wrap(native);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThatAsync(async () => await socket.CloseAsync(cancel.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Aborted));
        }

        [Test]
        public async Task ValidCloseRejectsQueuedAndNewDataBeforeItsOutputCanStart()
        {
            using var native = new HeldSocket();
            using var socket = Wrap(native);
            var active = socket.SendAsync(new byte[] { 1 }, false);
            await native.Sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = socket.SendAsync(new byte[] { 2 }, false);
            var closing = socket.CloseAsync();
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open), "Retain the public runtime state rather than adding a CloseRequested enum.");
            var late = socket.SendAsync(new byte[] { 3 }, false);
            native.ReleaseSend.TrySetResult(true);
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThatAsync(async () => await queued, Throws.InstanceOf<System.Net.WebSockets.WebSocketException>());
            await Assert.ThatAsync(async () => await late, Throws.InstanceOf<System.Net.WebSockets.WebSocketException>());
            Assert.That(native.SendCount, Is.EqualTo(1));
        }

        [Test]
        public async Task MessagesArrivingAfterCloseRequestDoNotInvokeApplicationCallbacks()
        {
            using var native = new HeldSocket();
            using var socket = Wrap(native);
            var active = socket.SendAsync(new byte[] { 1 }, false);
            await native.Sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = socket.CloseAsync();
            var context = DispatchProxy.Create<IWebSocketContext, ContextProxy>();
            ((ContextProxy)(object)context).Socket = socket;
            var module = new ProbeModule();
            var receiving = (Task)typeof(WebSocketModule).GetMethod("ProcessSystemContext", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(module, new object[] { context, socket, CancellationToken.None })!;
            await native.DataReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            native.ReleaseSend.TrySetResult(true);
            await Task.WhenAll(active, closing, receiving).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(module.Frames, Is.Zero);
            Assert.That(module.Messages, Is.Zero);
        }

        [Test]
        public async Task InvalidCloseParametersDoNotStopHealthyDataSends()
        {
            using var native = new HeldSocket();
            native.ReleaseSend.TrySetResult(true);
            using var socket = Wrap(native);
            Assert.Throws<ArgumentException>(() => socket.CloseAsync(CloseStatusCode.Normal, new string('x', 124)));
            Assert.Throws<ArgumentOutOfRangeException>(() => socket.CloseAsync((CloseStatusCode)42));
            await socket.SendAsync(new byte[] { 1 }, false);
            Assert.That(native.SendCount, Is.EqualTo(1));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
        }

        private static IWebSocket Wrap(System.Net.WebSockets.WebSocket socket)
            => (IWebSocket)Activator.CreateInstance(typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.SystemWebSocket", true)!, socket)!;

        public class ContextProxy : DispatchProxy
        {
            public IWebSocket Socket { get; set; } = null!;
            protected override object? Invoke(MethodInfo? method, object?[]? args)
                => method!.Name == "get_WebSocket" ? Socket : null;
        }

        private sealed class ProbeModule : WebSocketModule
        {
            public ProbeModule() : base("/", true) { }
            public int Frames;
            public int Messages;
            protected override Task OnFrameReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            { Interlocked.Increment(ref Frames); return Task.CompletedTask; }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            { Interlocked.Increment(ref Messages); return Task.CompletedTask; }
        }

        private sealed class HeldSocket : System.Net.WebSockets.WebSocket
        {
            private int _state = (int)WebSocketState.Open;
            private int _receives;
            public int SendCount;
            public int AbortCount;
            public TaskCompletionSource<bool> Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> DataReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> PeerClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
            public override string? CloseStatusDescription => null;
            public override string? SubProtocol => null;
            public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
            public override void Abort() { Interlocked.Increment(ref AbortCount); Dispose(); }
            public override void Dispose() { Volatile.Write(ref _state, (int)WebSocketState.Aborted); ReleaseSend.TrySetResult(true); PeerClose.TrySetResult(true); }
            public override Task CloseAsync(WebSocketCloseStatus status, string? reason, CancellationToken token)
            {
                if (Encoding.UTF8.GetByteCount(reason ?? "") > 123) throw new ArgumentException("Invalid close reason.");
                return CompleteUnixCloseAsync(token);
            }
            private async Task CompleteUnixCloseAsync(CancellationToken token)
            {
                await ReleaseSend.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                await CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
                await ReceiveAsync(new ArraySegment<byte>(new byte[1]), token);
            }
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string? reason, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Volatile.Write(ref _state, (int)WebSocketState.CloseSent);
                PeerClose.TrySetResult(true);
                return Task.CompletedTask;
            }
            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
            {
                if (Interlocked.Increment(ref _receives) == 1 && State == WebSocketState.Open)
                {
                    buffer.Array![buffer.Offset] = 112;
                    DataReceived.TrySetResult(true);
                    return new WebSocketReceiveResult(1, WebSocketMessageType.Text, true);
                }
                await PeerClose.Task.WaitAsync(token);
                Volatile.Write(ref _state, (int)WebSocketState.Closed);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
            {
                if (State != WebSocketState.Open) throw new System.Net.WebSockets.WebSocketException(WebSocketError.InvalidState);
                Interlocked.Increment(ref SendCount);
                Sending.TrySetResult(true);
                await ReleaseSend.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        }
    }
}
