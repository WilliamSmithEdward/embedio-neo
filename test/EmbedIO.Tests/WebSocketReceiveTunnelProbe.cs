using System;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    internal sealed class WebSocketReceiveTunnelProbe : WebSocketModule
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string _policy;
        private readonly TaskCompletionSource<bool>[] _started = Enumerable.Range(0, 2).Select(_ => Signal()).ToArray();
        private readonly TaskCompletionSource<bool>[] _finish = Enumerable.Range(0, 2).Select(_ => Signal()).ToArray();
        private readonly byte[][] _payloads = new byte[2][];
        internal readonly TaskCompletionSource<bool> Disconnected = Signal();
        internal int RemotePort;
        private int _calls;
        private IWebSocketContext? _acceptedContext;

        internal WebSocketReceiveTunnelProbe(string policy) : base("/ws", false)
        {
            _policy = policy;
            ReceiveLimits = policy switch
            {
                "count" => new WebSocketReceiveLimits(1, null, 2, null),
                "bytes" => new WebSocketReceiveLimits(null, 3, null, 3),
                "four-null" => new WebSocketReceiveLimits(),
                _ => throw new ArgumentException("Unknown first-party policy case.", nameof(policy)),
            };
        }

        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task OnClientConnectedAsync(IWebSocketContext context)
        {
            RemotePort = context.RemoteEndPoint.Port;
            _acceptedContext = context;
            return Task.CompletedTask;
        }

        protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
        {
            Disconnected.TrySetResult(true);
            return Task.CompletedTask;
        }

        protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            if (index >= _payloads.Length) throw new AssertionException("An unadmitted message reached the callback.");
            _payloads[index] = buffer;
            _started[index].TrySetResult(true);
            return _finish[index].Task;
        }

        private object? Budget => (typeof(WebSocketModule).GetProperty("DeliveryBudget", Hidden)
            ?? throw new AssertionException("Missing module budget capability.")).GetValue(this);

        private (long Messages, long Bytes) Outstanding()
        {
            var budget = Budget ?? throw new AssertionException("Missing finite tunnel budget.");
            var state = budget.GetType().GetProperty("Outstanding", Hidden)?.GetValue(budget)
                ?? throw new AssertionException("Missing budget snapshot.");
            return ((long)(state.GetType().GetProperty("Messages", Hidden)?.GetValue(state)
                    ?? throw new AssertionException("Missing count.")),
                (long)(state.GetType().GetProperty("Bytes", Hidden)?.GetValue(state)
                    ?? throw new AssertionException("Missing payload bytes.")));
        }

        private async Task SettlesAsync(long messages, long bytes, CancellationToken token)
        {
            while (Outstanding() != (messages, bytes)) await Task.Delay(1, token);
            Assert.That(Outstanding(), Is.EqualTo((messages, bytes)));
        }

        private void AssertNoLedger()
        {
            Assert.That(Budget, Is.Null);
            var socket = _acceptedContext?.WebSocket ?? throw new AssertionException("Missing accepted managed tunnel.");
            var field = socket.GetType().GetField("_deliveryBudget", Hidden)
                ?? throw new AssertionException("Missing managed socket budget field.");
            Assert.That(field.GetValue(socket), Is.Null);
        }

        internal async Task ExerciseAsync(System.Net.WebSockets.WebSocket peer, Func<Task> healthySibling,
            Func<Task> finishCarrier, CancellationToken token)
        {
            var first = _policy == "count" ? new byte[] { 41 } : new byte[] { 41, 42 };
            await peer.SendAsync(new ArraySegment<byte>(first), WebSocketMessageType.Binary, true, token);
            await _started[0].Task.WaitAsync(token);
            Assert.That(_payloads[0], Is.EqualTo(first));
            Assert.That(_finish[0].Task.IsCompleted, Is.False);
            await healthySibling();

            if (_policy == "four-null")
            {
                AssertNoLedger();
                await peer.SendAsync(new ArraySegment<byte>(new byte[] { 51, 52, 53 }), WebSocketMessageType.Binary, true, token);
                await _started[1].Task.WaitAsync(token);
                Assert.That(_calls, Is.EqualTo(2));
                Assert.That(_finish[0].Task.IsCompleted, Is.False, "Legacy callbacks still overlap.");
                AssertNoLedger();
                await healthySibling();
                await peer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", token);
                await Disconnected.Task.WaitAsync(token);
                await finishCarrier();
                Assert.That(_finish[0].Task.IsCompleted, Is.False, "Close does not join application callback Tasks.");
                Assert.That(_finish[1].Task.IsCompleted, Is.False);
                AssertNoLedger();
                _finish[1].SetResult(true);
                _finish[0].SetResult(true);
                Assert.That(_payloads[0], Is.EqualTo(first));
                Assert.That(_payloads[1], Is.EqualTo(new byte[] { 51, 52, 53 }));
                await healthySibling();
                return;
            }

            await SettlesAsync(1, first.Length, token);
            if (_policy == "count")
            {
                // An empty message has zero bytes but still needs count admission.
                await peer.SendAsync(new ArraySegment<byte>(Array.Empty<byte>()), WebSocketMessageType.Binary, true, token);
            }
            else
            {
                await peer.SendAsync(new ArraySegment<byte>(new byte[] { 61 }), WebSocketMessageType.Binary, false, token);
                await SettlesAsync(2, 3, token);
                Assert.That(_calls, Is.EqualTo(1), "The unfinished fragment is not delivered.");
                await peer.SendAsync(new ArraySegment<byte>(new byte[] { 62 }), WebSocketMessageType.Binary, true, token);
            }
            var close = await peer.ReceiveAsync(new ArraySegment<byte>(new byte[32]), token);
            Assert.That(close.MessageType, Is.EqualTo(WebSocketMessageType.Close));
            Assert.That(close.CloseStatus, Is.EqualTo(WebSocketCloseStatus.PolicyViolation));
            await peer.CloseAsync(WebSocketCloseStatus.PolicyViolation, null, token);
            await Disconnected.Task.WaitAsync(token);
            await finishCarrier();
            await SettlesAsync(1, first.Length, token);
            Assert.That(_calls, Is.EqualTo(1));
            Assert.That(_finish[0].Task.IsCompleted, Is.False, "Tunnel closure cannot release an unfinished callback.");
            Assert.That(_payloads[0], Is.EqualTo(first));
            await healthySibling();
            _finish[0].SetResult(true);
            await SettlesAsync(0, 0, token);
            Assert.That(_payloads[0], Is.EqualTo(first), "Application-retained data is not reclaimed by the budget.");
            await healthySibling();
        }

        internal void ReleaseAll()
        {
            foreach (var finish in _finish) finish.TrySetResult(true);
        }
    }
}
