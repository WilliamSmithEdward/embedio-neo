using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue556_EarlyWebSocketMessages
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public async Task MessagesReceivedBeforeSubscriptionAreDeliveredAfterSubscription()
        {
            var socket = CreateSocket();
            Enqueue(socket, 1);
            Enqueue(socket, 2);
            socket.GetType().GetMethod("Message", PrivateInstance)!.Invoke(socket, null);
            using var observer = new Observer();
            Subscribe(socket, observer);
            socket.GetType().GetMethod("Message", PrivateInstance)!.Invoke(socket, null);
            await observer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(observer.Values.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
        }

        private static object CreateSocket()
        {
            var assembly = typeof(WebServer).Assembly;
            var type = assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket", true)!;
            var eventType = assembly.GetType("EmbedIO.WebSockets.Internal.MessageEventArgs", true)!;
            var socket = RuntimeHelpers.GetUninitializedObject(type);
            GC.SuppressFinalize(socket);
            type.GetField("_readyState", PrivateInstance)!.SetValue(socket, WebSocketState.Open);
            type.GetField("_stateSyncRoot", PrivateInstance)!.SetValue(socket, new object());
            type.GetField("_messageSyncRoot", PrivateInstance)?.SetValue(socket, new object());
            var queue = Activator.CreateInstance(typeof(ConcurrentQueue<>).MakeGenericType(eventType))!;
            type.GetField("_messageEventQueue", PrivateInstance)!.SetValue(socket, queue);
            return socket;
        }

        private static void Enqueue(object socket, byte value)
        {
            var assembly = typeof(WebServer).Assembly;
            var frame = Activator.CreateInstance(assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true)!, PrivateInstance, null,
                new object[] { Enum.Parse(assembly.GetType("EmbedIO.WebSockets.Internal.Fin", true)!, "Final"), Opcode.Binary, new byte[] { value }, false }, null)!;
            var message = Activator.CreateInstance(assembly.GetType("EmbedIO.WebSockets.Internal.MessageEventArgs", true)!, PrivateInstance, null, new[] { frame }, null)!;
            var queue = socket.GetType().GetField("_messageEventQueue", PrivateInstance)!.GetValue(socket)!;
            queue.GetType().GetMethod("Enqueue")!.Invoke(queue, new[] { message });
        }

        private static void Subscribe(object socket, Observer observer)
        {
            var eventType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.MessageEventArgs", true)!;
            var messageEvent = socket.GetType().GetEvent("OnMessage")!;
            var sender = Expression.Parameter(typeof(object));
            var args = Expression.Parameter(eventType);
            var handler = Expression.Lambda(messageEvent.EventHandlerType!, Expression.Call(Expression.Constant(observer), typeof(Observer).GetMethod(nameof(Observer.Handle))!, sender, Expression.Convert(args, typeof(object))), sender, args).Compile();
            messageEvent.AddEventHandler(socket, handler);
        }

        public sealed class Observer : IDisposable
        {
            private readonly int _expected;
            private readonly bool _block;
            private readonly ManualResetEventSlim _release = new(false);
            public Observer(int expected = 2, bool block = false) { _expected = expected; _block = block; }
            public ConcurrentQueue<byte> Values { get; } = new();
            public TaskCompletionSource<bool> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Handle(object? sender, object args)
            {
                Values.Enqueue(((byte[])args.GetType().GetProperty("RawData")!.GetValue(args)!)[0]);
                if (Values.Count == 1)
                {
                    First.TrySetResult(true);
                    if (_block) _release.Wait(TimeSpan.FromSeconds(3));
                }
                if (Values.Count == _expected) Completed.TrySetResult(true);
            }
            public void Release() => _release.Set();
            public void Dispose() => _release.Dispose();
        }

        [Test]
        public async Task ConcurrentEnqueuesDuringDispatchAreDeliveredOnceWithoutAnExtraWakeup()
        {
            var socket = CreateSocket();
            Enqueue(socket, 0);
            using var observer = new Observer(64, true);
            Subscribe(socket, observer);
            await observer.First.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var producers = Enumerable.Range(1, 63).Select(i => Task.Run(() =>
            {
                Enqueue(socket, (byte)i);
                socket.GetType().GetMethod("Message", PrivateInstance)!.Invoke(socket, null);
            })).ToArray();
            observer.Release();
            await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(3));
            await observer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(observer.Values.OrderBy(x => x).ToArray(), Is.EqualTo(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        }

        [TestCase(HttpListenerMode.EmbedIO, 0)]
        [TestCase(HttpListenerMode.EmbedIO, 1)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        [TestCase(HttpListenerMode.EmbedIO, 3)]
        [TestCase(HttpListenerMode.EmbedIO, 4)]
        [TestCase(HttpListenerMode.EmbedIO, 5)]
        [TestCase(HttpListenerMode.Microsoft, 0)]
        [TestCase(HttpListenerMode.Microsoft, 1)]
        [TestCase(HttpListenerMode.Microsoft, 2)]
        [TestCase(HttpListenerMode.Microsoft, 3)]
        [TestCase(HttpListenerMode.Microsoft, 4)]
        [TestCase(HttpListenerMode.Microsoft, 5)]
        public async Task ClientCanSendWhileTheConnectionCallbackIsPending(HttpListenerMode mode, int shape)
        {
            var url = Resources.GetServerAddress();
            var binary = shape == 1 || shape == 4 || shape == 5;
            using var module = new GatedModule(binary);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(url.Replace("http", "ws") + "socket"), timeout.Token);
                await module.Connected.Task.WaitAsync(timeout.Token);
                for (var i = 0; i < 8; i++)
                {
                    var bytes = shape == 3 || shape == 4 ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(i.ToString());
                    var type = binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
                    if (shape == 2 || shape == 5)
                        await client.SendAsync(new ArraySegment<byte>(Array.Empty<byte>()), type, false, timeout.Token);
                    await client.SendAsync(new ArraySegment<byte>(bytes), type, true, timeout.Token);
                }
                var receive = client.ReceiveAsync(new ArraySegment<byte>(new byte[16]), timeout.Token);
                if (mode == HttpListenerMode.EmbedIO)
                {
                    module.BeginPing.TrySetResult(true);
                    await module.PingCompleted.Task.WaitAsync(timeout.Token);
                }
                Assert.That(module.Messages.Count, Is.Zero, "Application dispatch must follow connection initialization.");
                module.Release.TrySetResult(true);
                await module.AllMessages.Task.WaitAsync(timeout.Token);
                Assert.That(module.Messages.ToArray(), Is.EqualTo((shape == 3 || shape == 4 ? Enumerable.Repeat(string.Empty, 8) : Enumerable.Range(0, 8).Select(i => i.ToString())).ToArray()));
                var result = await receive;
                Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Text));
                for (var i = 1; i < 8; i++)
                    Assert.That(await ReceiveText(client, timeout.Token), Is.EqualTo("ACK"));
                Assert.That(module.ActiveCount, Is.EqualTo(1));
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                await module.Disconnected.Task.WaitAsync(timeout.Token);
            }
            finally { module.Release.TrySetResult(true); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private sealed class GatedModule : WebSocketModule
        {
            private readonly bool _binary;
            public GatedModule(bool binary) : base("/socket", false) => _binary = binary;
            public TaskCompletionSource<bool> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> BeginPing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> PingCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> AllMessages { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ConcurrentQueue<string> Messages { get; } = new();
            public int ActiveCount => ActiveContexts.Count;
            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Connected.TrySetResult(true);
                if (context.WebSocket.GetType().Name == "WebSocket")
                {
                    await BeginPing.Task.WaitAsync(context.CancellationToken);
                    var frameType = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true)!;
                    var ping = (byte[])frameType.GetField("EmptyPingBytes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                    var pending = (Task<bool>)context.WebSocket.GetType().GetMethod("PingAsync", PrivateInstance)!.Invoke(context.WebSocket, new object[] { ping, TimeSpan.FromSeconds(3) })!;
                    Assert.That(await pending, Is.True);
                    PingCompleted.TrySetResult(true);
                }
                await Release.Task.WaitAsync(context.CancellationToken);
                context.Items["initialized"] = true;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                Assert.That(context.Items.ContainsKey("initialized"), Is.True);
                Assert.That(result.MessageType, Is.EqualTo((int)(_binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text)));
                Messages.Enqueue(Encoding.ASCII.GetString(buffer));
                if (Messages.Count == 8) AllMessages.TrySetResult(true);
                return SendAsync(context, "ACK");
            }
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Disconnected.TrySetResult(true);
                return Task.CompletedTask;
            }
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task FailedConnectionCallbackRemovesItsAcceptedContext(HttpListenerMode mode, bool canceled)
        {
            var url = Resources.GetServerAddress();
            using var module = new LifetimeModule(canceled ? "cancel" : "throw");
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(new Uri(url.Replace("http", "ws") + "socket"), timeout.Token);
                module.Release.TrySetResult(true);
                await module.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(module.ActiveCount, Is.Zero);
                Assert.That(module.DisconnectCount, Is.EqualTo(1));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private sealed class LifetimeModule : WebSocketModule
        {
            private readonly string _failure;
            private int _disconnectCount;
            public LifetimeModule(string failure) : base("/socket", false) => _failure = failure;
            public TaskCompletionSource<bool> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int ActiveCount => ActiveContexts.Count;
            public int DisconnectCount => Volatile.Read(ref _disconnectCount);
            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Connected.TrySetResult(true);
                await Release.Task.WaitAsync(context.CancellationToken);
                if (_failure == "cancel") throw new TaskCanceledException("controlled initialization cancellation");
                if (_failure == "throw") throw new InvalidOperationException("controlled initialization failure");
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) => Task.CompletedTask;
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Interlocked.Increment(ref _disconnectCount);
                Disconnected.TrySetResult(true);
                return Task.CompletedTask;
            }
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task CloseOrStopDuringInitializationCleansUpOnce(HttpListenerMode mode, bool shutdown)
        {
            var url = Resources.GetServerAddress();
            using var module = new LifetimeModule(string.Empty);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(url.Replace("http", "ws") + "socket"), timeout.Token);
                await module.Connected.Task.WaitAsync(timeout.Token);
                if (shutdown)
                {
                    stop.Cancel();
                    await running.WaitAsync(timeout.Token);
                }
                else
                {
                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                    module.Release.TrySetResult(true);
                    var close = await client.ReceiveAsync(new ArraySegment<byte>(new byte[16]), timeout.Token);
                    Assert.That(close.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                }
                await module.Disconnected.Task.WaitAsync(timeout.Token);
                Assert.That(module.ActiveCount, Is.Zero);
                Assert.That(module.DisconnectCount, Is.EqualTo(1));
            }
            finally { module.Release.TrySetResult(true); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task ImmediateMessagesFromIndependentClientsRemainIsolated(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var module = new EchoModule();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var first = new ClientWebSocket();
                using var second = new ClientWebSocket();
                var endpoint = new Uri(url.Replace("http", "ws") + "socket");
                await Task.WhenAll(Exercise(first, "first"), Exercise(second, "second"));
                await first.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                await module.FirstDisconnected.Task.WaitAsync(timeout.Token);
                await SendText(second, "after-peer-close", timeout.Token);
                Assert.That(await ReceiveText(second, timeout.Token), Is.EqualTo("after-peer-close"));
                await second.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token);
                await module.AllDisconnected.Task.WaitAsync(timeout.Token);

                async Task Exercise(ClientWebSocket client, string identity)
                {
                    await client.ConnectAsync(endpoint, timeout.Token);
                    for (var i = 0; i < 10; i++)
                    {
                        var message = identity + i;
                        await SendText(client, message, timeout.Token);
                        Assert.That(await ReceiveText(client, timeout.Token), Is.EqualTo(message));
                    }
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [Test]
        public async Task ManagedAsyncMessageCallbackFailureDoesNotEscapeTheEventBoundary()
        {
            var url = Resources.GetServerAddress();
            using var module = new EchoModule();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(url.Replace("http", "ws") + "socket"), timeout.Token);
                await SendText(client, "fault", timeout.Token);
                await module.FaultAttempted.Task.WaitAsync(timeout.Token);
                await SendText(client, "healthy", timeout.Token);
                Assert.That(await ReceiveText(client, timeout.Token), Is.EqualTo("healthy"));
                client.Abort();
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static Task SendText(ClientWebSocket client, string text, CancellationToken cancellation)
            => client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancellation);

        private static async Task<string> ReceiveText(ClientWebSocket client, CancellationToken cancellation)
        {
            var buffer = new byte[64];
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Text));
            Assert.That(result.EndOfMessage, Is.True);
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }

        private sealed class EchoModule : WebSocketModule
        {
            private int _disconnectCount;
            public EchoModule() : base("/socket", false) { }
            public TaskCompletionSource<bool> FaultAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> FirstDisconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> AllDisconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override async Task OnClientConnectedAsync(IWebSocketContext context) => await Task.Yield();
            protected override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                var text = Encoding.UTF8.GetString(buffer);
                if (text == "fault")
                {
                    await Task.Yield();
                    FaultAttempted.TrySetResult(true);
                    throw new InvalidOperationException("controlled asynchronous message failure");
                }
                await SendAsync(context, text);
            }
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                FirstDisconnected.TrySetResult(true);
                if (Interlocked.Increment(ref _disconnectCount) == 2) AllDisconnected.TrySetResult(true);
                return Task.CompletedTask;
            }
        }
    }
}
