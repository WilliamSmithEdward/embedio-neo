using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue534_WebSocketSends
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public async Task CancelledCloseDrainsPendingSendsAndDisposesGatesAfterTheirRelease()
        {
            using var transport = new ControlledStream();
            var socket = CreateSocket(transport);
            var data = socket.SendAsync(new byte[2033], false);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = socket.SendAsync(new byte[1017], true);
            using var cancelled = new CancellationTokenSource();
            var closing = socket.CloseAsync(cancelled.Token);
            cancelled.Cancel();
            try
            {
                await Assert.ThatAsync(async () => await closing, Throws.InstanceOf<OperationCanceledException>());
                Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
                Assert.That(((socket).GetType().GetField("_sendGatesDisposed", Private) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(socket), Is.False);
            }
            finally { transport.Release.TrySetResult(true); }
            await Task.WhenAll(data, queued).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(transport.FrameHeaders.ToArray(), Is.EqualTo(new byte[] { 2 }));
            Assert.That(transport.CloseCount, Is.EqualTo(1));
            Assert.That(((socket).GetType().GetField("_sendGatesDisposed", Private) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(socket), Is.True);
        }

        [Test]
        public async Task DisposeWaitsForTheActiveFrameThenClosesWithoutAppendingContinuationFrames()
        {
            using var transport = new ControlledStream();
            var socket = CreateSocket(transport);
            var data = socket.SendAsync(new byte[2033], false);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposing = Task.Run(socket.Dispose);
            try
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (socket.State == WebSocketState.Open) await Task.Delay(1, wait.Token);
            }
            finally { transport.Release.TrySetResult(true); }
            await Task.WhenAll(data, disposing).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(transport.FrameHeaders.ToArray(), Is.EqualTo(new byte[] { 2, 136 }));
            Assert.That(transport.MaximumConcurrentWrites, Is.EqualTo(1));
            Assert.That(transport.CloseCount, Is.EqualTo(1));
            Assert.That(((socket).GetType().GetField("_sendGatesDisposed", Private) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(socket), Is.True);
        }

        [Test]
        public async Task CancellationBetweenFragmentsTerminatesTheUnfinishedMessage()
        {
            using var cancelled = new CancellationTokenSource();
            using var transport = new ControlledStream { AfterFirstWrite = cancelled.Cancel };
            transport.Release.TrySetResult(true);
            var socket = CreateSocket(transport);
            await Assert.ThatAsync(async () => await socket.SendAsync(new byte[2033], false, cancelled.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
            Assert.That(transport.FrameHeaders.ToArray(), Is.EqualTo(new byte[] { 2 }));
            Assert.That(transport.CloseCount, Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task RepeatedCloseReconnectAndServerShutdownNotifyEachDisconnectOnce(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            var module = new SendModule();
            server.WithModule(module);
            var running = server.RunAsync(stop.Token);
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    using var client = new ClientWebSocket();
                    await client.ConnectAsync(new Uri(url.Replace("http://", "ws://", StringComparison.Ordinal)), timeout.Token);
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", timeout.Token);
                }
                stop.Cancel();
                await running.WaitAsync(timeout.Token);
                while (module.Disconnects.Count < 3) await Task.Delay(1, timeout.Token);
                Assert.That(module.Disconnects.Values, Is.EqualTo(new[] { 1, 1, 1 }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase("pong")]
        [TestCase("ping")]
        [TestCase("close")]
        public async Task ControlFramesCannotOverlapAnActiveTransportWrite(string kind)
        {
            using var transport = new ControlledStream();
            var socket = CreateSocket(transport);
            var data = socket.SendAsync(new byte[2033], false);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var type = socket.GetType();
            Task control;
            if (kind == "close") control = socket.CloseAsync();
            else if (kind == "ping") control = (Task)((((type).GetMethod("PingAsync", Private, null,
                new[] { typeof(byte[]), typeof(TimeSpan) }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(socket, new object[] { new byte[] { 137, 0 }, TimeSpan.Zero })) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            else
            {
                var assembly = type.Assembly;
                var frame = Activator.CreateInstance((assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrame", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")),
                    Private | BindingFlags.Public, null,
                    new[] { Enum.Parse((assembly.GetType("EmbedIO.WebSockets.Opcode", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), "Pong"),
                        Activator.CreateInstance((assembly.GetType("EmbedIO.WebSockets.Internal.PayloadData", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")),
                            Private | BindingFlags.Public, null, new object[] { Array.Empty<byte>() }, null) }, null);
                control = (Task)((((type).GetMethod("Send", Private) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(socket, new[] { frame })) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            }
            transport.Release.TrySetResult(true);
            await Task.WhenAll(data, (control)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(transport.MaximumConcurrentWrites, Is.EqualTo(1));
            var headers = transport.FrameHeaders.ToArray();
            Assert.That(headers.Count(h => (h & 15) == (kind == "pong" ? 10 : kind == "ping" ? 9 : 8)), Is.EqualTo(1));
            if (kind == "close") Assert.That(headers, Is.EqualTo(new byte[] { 2, 136 }));
            else await socket.CloseAsync();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InterruptedTransportIsTerminalAndQueuedSendsDrainBeforeGateDisposal(bool failWrite)
        {
            using var transport = new ControlledStream { FailWrite = failWrite };
            var socket = CreateSocket(transport);
            using var cancelled = new CancellationTokenSource();
            var active = socket.SendAsync(new byte[2033], false, cancelled.Token);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = socket.SendAsync(new byte[1017], true);
            if (failWrite) transport.Release.TrySetResult(true);
            else cancelled.Cancel();
            if (failWrite) await Assert.ThatAsync(async () => await active, Throws.TypeOf<IOException>());
            else await Assert.ThatAsync(async () => await active, Throws.InstanceOf<OperationCanceledException>());
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(socket.State, Is.EqualTo(WebSocketState.Closed));
            Assert.That(transport.CloseCount, Is.EqualTo(1));
            Assert.That(transport.FrameHeaders.Count, Is.EqualTo(1));
            Assert.That(((socket).GetType().GetField("_sendGatesDisposed", Private) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(socket), Is.True);
            socket.Dispose();
            Assert.That(transport.CloseCount, Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task ConcurrentRealMessagesRemainCompleteAndConnectionStaysUsable(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            var module = new SendModule();
            server.WithModule(module);
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(url.Replace("http://", "ws://", StringComparison.Ordinal)), timeout.Token);
                var context = await module.Connected.Task.WaitAsync(timeout.Token);
                var payloads = Enumerable.Range(0, 24).Select(i => Encoding.UTF8.GetBytes(i + ":\u20ac:" + new string((char)('a' + i), 9000))).ToArray();
                var sending = Task.WhenAll(payloads.Select((bytes, i) => context.WebSocket.SendAsync(bytes, i % 2 == 0, timeout.Token)));
                var messages = new System.Collections.Generic.HashSet<string>();
                for (var i = 0; i < payloads.Length; i++)
                {
                    using var data = new MemoryStream();
                    var buffer = new byte[713];
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                        Assert.That(result.MessageType, Is.AnyOf(WebSocketMessageType.Text, WebSocketMessageType.Binary));
                        data.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    var message = Encoding.UTF8.GetString(data.ToArray());
                    var index = int.Parse(message.Substring(0, message.IndexOf((':').ToString(), System.StringComparison.Ordinal)));
                    Assert.That(result.MessageType, Is.EqualTo(index % 2 == 0 ? WebSocketMessageType.Text : WebSocketMessageType.Binary));
                    Assert.That(messages.Add(message), Is.True);
                }
                await sending;
                Assert.That(messages, Is.EquivalentTo(payloads.Select(Encoding.UTF8.GetString)));
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [Test]
        public async Task FragmentedMessagesDoNotOverlapTransportWrites()
        {
            using var transport = new ControlledStream();
            var socket = CreateSocket(transport);
            var first = socket.SendAsync(new byte[3049], false);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = socket.SendAsync(new byte[2033], true);
            transport.Release.TrySetResult(true);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(transport.MaximumConcurrentWrites, Is.EqualTo(1));
            var headers = transport.FrameHeaders.ToArray();
            Assert.That(headers, Is.EqualTo(new byte[] { 2, 0, 0, 128, 1, 0, 128 }),
                "Every fragmented message must finish before the next data message starts.");
        }

        [Test]
        public async Task PreCancelledSendWritesNoFrames()
        {
            using var transport = new ControlledStream();
            transport.Release.TrySetResult(true);
            var socket = CreateSocket(transport);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThatAsync(async () => await socket.SendAsync(new byte[2033], false, cancelled.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(transport.FrameHeaders, Is.Empty);
        }

        [Test]
        public async Task QueuedCancellationDoesNotReleaseAnotherSendPermit()
        {
            using var transport = new ControlledStream();
            var socket = CreateSocket(transport);
            var first = socket.SendAsync(new byte[2033], false);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelled = new CancellationTokenSource();
            var queued = socket.SendAsync(new byte[1017], true, cancelled.Token);
            cancelled.Cancel();
            try
            {
                await Assert.ThatAsync(async () => await queued.WaitAsync(TimeSpan.FromSeconds(5)),
                    Throws.InstanceOf<OperationCanceledException>());
                Assert.That(transport.FrameHeaders.Count, Is.EqualTo(1));
            }
            finally
            {
                transport.Release.TrySetResult(true);
                await first.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await socket.SendAsync(new byte[] { 42 }, false).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(transport.MaximumConcurrentWrites, Is.EqualTo(1));
            Assert.That(transport.FrameHeaders.ToArray(), Is.EqualTo(new byte[] { 2, 0, 128, 130 }));
        }

        private static IWebSocket CreateSocket(Stream stream)
        {
            var assembly = typeof(WebServer).Assembly;
            var connectionType = assembly.GetType("EmbedIO.Net.Internal.HttpConnection", true);
            var connection = RuntimeHelpers.GetUninitializedObject((connectionType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")));
            GC.SuppressFinalize(connection);
            ((connectionType).GetField("<Stream>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(connection, stream);
            var socketType = assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket", true);
            var socket = (IWebSocket)(Activator.CreateInstance((socketType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { connection }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            ((socketType).GetField("_closeConnection", Private) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).SetValue(socket,
                new Action(() => ((ControlledStream)stream).RecordClose()));
            return (socket);
        }

        private sealed class SendModule : WebSocketModule
        {
            public SendModule() : base("/", true) { }
            public TaskCompletionSource<IWebSocketContext> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ConcurrentDictionary<string, int> Disconnects { get; } = new();
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Connected.TrySetResult(context);
                return Task.CompletedTask;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => Task.CompletedTask;
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Disconnects.AddOrUpdate(context.Id, 1, (_, count) => count + 1);
                return Task.CompletedTask;
            }
        }

        private sealed class ControlledStream : Stream
        {
            private int _writes;
            private int _active;
            private int _maximum;
            private int _closeCount;
            public int CloseCount => Volatile.Read(ref _closeCount);
            public void RecordClose() => Interlocked.Increment(ref _closeCount);
            public bool FailWrite { get; set; }
            public Action? AfterFirstWrite { get; set; }
            public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ConcurrentQueue<byte> FrameHeaders { get; } = new();
            public int MaximumConcurrentWrites => _maximum;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Expected asynchronous transport I/O.");
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = Interlocked.Increment(ref _active);
                var previous = Volatile.Read(ref _maximum);
                while (active > previous)
                {
                    var observed = Interlocked.CompareExchange(ref _maximum, active, previous);
                    if (observed == previous) break;
                    previous = observed;
                }
                try
                {
                    FrameHeaders.Enqueue(buffer[offset]);
                    if (Interlocked.Increment(ref _writes) == 1)
                    {
                        Started.TrySetResult(true);
                        await Release.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                        AfterFirstWrite?.Invoke();
                    }
                    if (FailWrite) throw new IOException("Deliberate transport failure.");
                }
                finally { Interlocked.Decrement(ref _active); }
            }
        }
    }
}
