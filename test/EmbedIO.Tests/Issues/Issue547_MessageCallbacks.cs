using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue547_MessageCallbacks
    {
        [TestCase(HttpListenerMode.EmbedIO, 0)]
        [TestCase(HttpListenerMode.EmbedIO, 1)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        [TestCase(HttpListenerMode.EmbedIO, 3)]
        [TestCase(HttpListenerMode.Microsoft, 0)]
        [TestCase(HttpListenerMode.Microsoft, 1)]
        [TestCase(HttpListenerMode.Microsoft, 2)]
        [TestCase(HttpListenerMode.Microsoft, 3)]
        public async Task CompleteMessagesPreserveEmptyBinaryAndSplitUnicode(HttpListenerMode mode, int shape)
        {
            using var fixture = new Fixture(mode);
            using var client = await fixture.Connect();
            var binary = shape % 2 == 1;
            var data = shape < 2 ? Array.Empty<byte>() : binary
                ? Enumerable.Range(0, 20000).Select(i => (byte)i).ToArray()
                : Encoding.UTF8.GetBytes("split € 😀 " + new string('x', 10000));
            var type = binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
            var split = Math.Min(7, data.Length);
            await client.SendAsync(new ArraySegment<byte>(data, 0, split), type, false, fixture.Timeout.Token);
            await client.SendAsync(new ArraySegment<byte>(data, split, data.Length - split), type, true, fixture.Timeout.Token);
            var reply = await Read(client, fixture.Timeout.Token);
            Assert.That(reply.Type, Is.EqualTo(type));
            Assert.That(reply.Data, Is.EqualTo(data));
            Assert.That(fixture.Module.Messages.Count, Is.EqualTo(1));
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", fixture.Timeout.Token);
        }

        [TestCase(HttpListenerMode.EmbedIO, "unsupported", 1003)]
        [TestCase(HttpListenerMode.Microsoft, "unsupported", 1003)]
        [TestCase(HttpListenerMode.EmbedIO, "invalid", 1007)]
        [TestCase(HttpListenerMode.Microsoft, "invalid", 1007)]
        [TestCase(HttpListenerMode.EmbedIO, "oversize", 1009)]
        [TestCase(HttpListenerMode.Microsoft, "oversize", 1009)]
        [TestCase(HttpListenerMode.EmbedIO, "failure", 1011)]
        [TestCase(HttpListenerMode.Microsoft, "failure", 1011)]
        [TestCase(HttpListenerMode.EmbedIO, "away", 1001)]
        [TestCase(HttpListenerMode.Microsoft, "away", 1001)]
        public async Task RejectionCodesAreObservableAndAnotherClientStaysHealthy(HttpListenerMode mode, string scenario, int expected)
        {
            using var fixture = new Fixture(mode, scenario);
            using var client = await fixture.Connect();
            using var trace = new CapturingTraceListener();
            EmbedIO.Diagnostics.Log.Source.Listeners.Add(trace);
            var data = scenario == "invalid" ? new byte[] { 0xc0, 0xaf }
                : Encoding.UTF8.GetBytes(scenario == "oversize" ? "12345" : scenario == "failure" ? "fail" : scenario == "away" ? "away" : "binary");
            try
            {
                await client.SendAsync(new ArraySegment<byte>(data), scenario == "unsupported" ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, fixture.Timeout.Token);
                try
                {
                    var result = await client.ReceiveAsync(new ArraySegment<byte>(new byte[256]), fixture.Timeout.Token);
                    Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                    Assert.That((int?)result.CloseStatus, Is.EqualTo(expected));
                    await client.CloseOutputAsync(result.CloseStatus ?? throw new AssertionException("Expected a close status."), "ack", fixture.Timeout.Token);
                }
                catch (System.Net.WebSockets.WebSocketException error) when (scenario == "invalid"
                    && mode == HttpListenerMode.Microsoft && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    && error.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    Assert.That(fixture.Module.Messages, Is.Empty, "The native runtime rejects invalid text before application dispatch.");
                    await fixture.Module.Disconnected.Task.WaitAsync(fixture.Timeout.Token);
                    var diagnostics = trace.Snapshot();
                    TestContext.Out.WriteLine("Native invalid UTF-8 diagnostics: " + diagnostics);
                }
            }
            finally { EmbedIO.Diagnostics.Log.Source.Listeners.Remove(trace); }
            using var healthy = await fixture.Connect();
            await healthy.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("ok")), WebSocketMessageType.Text, true, fixture.Timeout.Token);
            Assert.That(Encoding.UTF8.GetString((await Read(healthy, fixture.Timeout.Token)).Data), Is.EqualTo("ok"));
            await healthy.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", fixture.Timeout.Token);
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task BinaryOnlyEndpointsRejectTextUsingTheDefaultCallback(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var module = new BinaryOnly();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(url, "http", "ws") + "socket"), timeout.Token);
                await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("text")), WebSocketMessageType.Text, true, timeout.Token);
                var result = await client.ReceiveAsync(new ArraySegment<byte>(new byte[128]), timeout.Token);
                Assert.That((int?)result.CloseStatus, Is.EqualTo(1003));
                await client.CloseOutputAsync(result.CloseStatus ?? throw new AssertionException("Expected a close status."), "ack", timeout.Token);
            }
            finally { stop.Cancel(); await running.WaitAsync(timeout.Token); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task ExistingSubclassAndFrameCallbacksKeepTheirBehavior(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var module = new Legacy();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new ClientWebSocket();
                await client.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(url, "http", "ws") + "socket"), timeout.Token);
                var payload = new byte[10000];
                await client.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, timeout.Token);
                Assert.That((await Read(client, timeout.Token)).Data, Is.EqualTo(payload));
                Assert.That(module.Messages, Is.EqualTo(1));
                Assert.That(module.Frames > 0, Is.EqualTo(mode == HttpListenerMode.Microsoft));
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            finally { stop.Cancel(); await running.WaitAsync(timeout.Token); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task BurstsProduceOrderedCompleteEchoes(HttpListenerMode mode)
        {
            using var fixture = new Fixture(mode);
            using var client = await fixture.Connect();
            for (var i = 0; i < 32; i++)
                await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(i.ToString(System.Globalization.CultureInfo.InvariantCulture))), WebSocketMessageType.Text, true, fixture.Timeout.Token);
            for (var i = 0; i < 32; i++)
                Assert.That(Encoding.UTF8.GetString((await Read(client, fixture.Timeout.Token)).Data), Is.EqualTo(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", fixture.Timeout.Token);
        }

        private sealed class BinaryOnly : WebSocketMessageModule
        {
            public BinaryOnly() : base("/socket", false) { }
            protected override Task OnBinaryMessageReceivedAsync(IWebSocketContext context, byte[] data) => SendAsync(context, data);
        }

        private sealed class Legacy : WebSocketModule
        {
            public Legacy() : base("/socket", false) { }
            public int Frames { get; private set; }
            public int Messages { get; private set; }
            protected override Task OnFrameReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                Frames++;
                return Task.CompletedTask;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                Messages++;
                return SendAsync(context, buffer);
            }
        }

        [TestCase(CloseStatusCode.Normal, 1000)]
        [TestCase(CloseStatusCode.Away, 1001)]
        [TestCase(CloseStatusCode.ProtocolError, 1002)]
        [TestCase(CloseStatusCode.UnsupportedData, 1003)]
        [TestCase(CloseStatusCode.InvalidData, 1007)]
        [TestCase(CloseStatusCode.PolicyViolation, 1008)]
        [TestCase(CloseStatusCode.TooBig, 1009)]
        [TestCase(CloseStatusCode.MandatoryExtension, 1010)]
        [TestCase(CloseStatusCode.ServerError, 1011)]
        public void NativeCloseMappingsMatchTheirWireValues(CloseStatusCode code, int expected)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.SystemWebSocket", true);
            using var client = new ClientWebSocket();
            var wrapper = Activator.CreateInstance((type ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), new object[] { client });
            var mapped = ((type).GetMethod("MapCloseStatus", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(wrapper, new object[] { code });
            Assert.That((int)(WebSocketCloseStatus)(mapped ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), Is.EqualTo(expected));
            ((IDisposable)(wrapper ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))).Dispose();
        }

        [TestCase(new byte[] { 0xc0, 0xaf })]
        [TestCase(new byte[] { 0xed, 0xa0, 0x80 })]
        [TestCase(new byte[] { 0xf4, 0x90, 0x80, 0x80 })]
        [TestCase(new byte[] { 0xe2, 0x82 })]
        [TestCase(new byte[] { 0x80 })]
        public async Task InvalidTextDeliveredToTheModuleNeverReachesApplicationCode(byte[] data)
        {
            if (data is null) throw new System.NullReferenceException();
            using var module = new Echo("echo");
            var socket = new RecordingSocket();
            var context = DispatchProxy.Create<IWebSocketContext, ContextProxy>();
            ((ContextProxy)(object)context).Socket = socket;
            await module.Dispatch(context, data);
            Assert.That(socket.Code, Is.EqualTo(CloseStatusCode.InvalidData));
            Assert.That(module.Messages, Is.Empty);
        }

        [Test]
        public async Task AnApplicationDecoderExceptionIsAServerError()
        {
            using var module = new Echo("decoder-failure");
            var socket = new RecordingSocket();
            var context = DispatchProxy.Create<IWebSocketContext, ContextProxy>();
            ((ContextProxy)(object)context).Socket = socket;
            await module.Dispatch(context, Encoding.UTF8.GetBytes("valid"));
            Assert.That(socket.Code, Is.EqualTo(CloseStatusCode.ServerError));
        }

        public class ContextProxy : DispatchProxy
        {
            public IWebSocket? Socket { get; set; }
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod is null) throw new System.NullReferenceException();
                return targetMethod.Name == "get_WebSocket" ? Socket : targetMethod.Name == "get_CancellationToken" ? CancellationToken.None : null;
            }
        }

        private sealed class RecordingSocket : IWebSocket
        {
            public WebSocketState State { get; private set; } = WebSocketState.Open;
            public CloseStatusCode? Code { get; private set; }
            public void Dispose() => State = WebSocketState.Closed;
            public Task SendAsync(byte[] buffer, bool isText, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task CloseAsync(CancellationToken cancellationToken = default) => CloseAsync(CloseStatusCode.Normal, null, cancellationToken);
            public Task CloseAsync(CloseStatusCode code, string? comment = null, CancellationToken cancellationToken = default)
            {
                Code = code;
                State = WebSocketState.Closed;
                return Task.CompletedTask;
            }
        }

        private sealed class Result(int count) : IWebSocketReceiveResult
        {
            public int Count => count;
            public bool EndOfMessage => true;
            public int MessageType => 0;
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task AsyncCallbacksAreSequentialPerConnectionButIndependentAcrossConnections(HttpListenerMode mode)
        {
            using var fixture = new Fixture(mode, "gate");
            using var first = await fixture.Connect();
            using var second = await fixture.Connect();
            await first.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hold")), WebSocketMessageType.Text, true, fixture.Timeout.Token);
            await fixture.Module.Entered.Task.WaitAsync(fixture.Timeout.Token);
            await first.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("next")), WebSocketMessageType.Text, true, fixture.Timeout.Token);
            await second.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("independent")), WebSocketMessageType.Text, true, fixture.Timeout.Token);
            Assert.That(Encoding.UTF8.GetString((await Read(second, fixture.Timeout.Token)).Data), Is.EqualTo("independent"));
            Assert.That(fixture.Module.Messages.ToArray(), Does.Not.Contain("next"));
            fixture.Module.Release.TrySetResult(true);
            Assert.That(Encoding.UTF8.GetString((await Read(first, fixture.Timeout.Token)).Data), Is.EqualTo("hold"));
            Assert.That(Encoding.UTF8.GetString((await Read(first, fixture.Timeout.Token)).Data), Is.EqualTo("next"));
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", fixture.Timeout.Token);
            await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", fixture.Timeout.Token);
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task CancellationReleasesTheActiveCallbackAndServer(HttpListenerMode mode)
        {
            using var fixture = new Fixture(mode, "gate");
            var phase = "connect";
            try
            {
                using var client = await fixture.Connect();
                phase = "send hold";
                await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hold")), WebSocketMessageType.Text, true, fixture.Timeout.Token);
                phase = "wait callback entry";
                await fixture.Module.Entered.Task.WaitAsync(fixture.Timeout.Token);
                phase = "send next";
                await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("next")), WebSocketMessageType.Text, true, fixture.Timeout.Token);
                phase = "cancel server";
                fixture.Stop.Cancel();
                phase = "wait server completion";
                await fixture.Running.WaitAsync(fixture.Timeout.Token);
                phase = "wait callback completion";
                await fixture.Module.CallbackFinished.Task.WaitAsync(fixture.Timeout.Token);
                Assert.That(fixture.Module.Messages.ToArray(), Does.Not.Contain("next"));
            }
            catch (Exception error)
            {
                TestContext.Error.WriteLine($"Cancellation failure before cleanup: mode={mode}, phase={phase}, server={fixture.Running.Status}, callback={fixture.Module.CallbackFinished.Task.Status}, canceled={fixture.Stop.IsCancellationRequested}. {error}");
                throw;
            }
        }

        private static async Task<(WebSocketMessageType Type, byte[] Data)> Read(ClientWebSocket client, CancellationToken cancellation)
        {
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult result;
            do
            {
                result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
                Assert.That(result.MessageType, Is.Not.EqualTo(WebSocketMessageType.Close));
                output.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return (result.MessageType, output.ToArray());
        }

        private sealed class CapturingTraceListener : TraceListener
        {
            private readonly object _sync = new();
            private readonly StringBuilder _messages = new();

            public override void Write(string? message)
            {
                lock (_sync) _messages.Append(message);
            }

            public override void WriteLine(string? message)
            {
                lock (_sync) _messages.AppendLine(message);
            }

            internal string Snapshot()
            {
                lock (_sync) return _messages.ToString();
            }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly WebServer _server;
            private readonly string _url = Resources.GetServerAddress();
            public Fixture(HttpListenerMode mode, string scenario = "echo")
            {
                Module = new Echo(scenario);
                _server = new WebServer(o => o.WithUrlPrefix(_url).WithMode(mode)).WithModule(Module);
                Running = _server.RunAsync(Stop.Token);
            }
            public Echo Module { get; }
            public Task Running { get; }
            public CancellationTokenSource Stop { get; } = new();
            public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(15));
            public async Task<ClientWebSocket> Connect()
            {
                var client = new ClientWebSocket();
                try { await client.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(_url, "http", "ws") + "socket"), Timeout.Token); return client; }
                catch { client.Dispose(); throw; }
            }
            public void Dispose()
            {
                Module.Release.TrySetResult(true);
                Stop.Cancel();
                try { Running.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                finally { _server.Dispose(); Timeout.Dispose(); Stop.Dispose(); }
            }
        }

        private sealed class Echo : WebSocketMessageModule
        {
            private readonly string _scenario;
            public Echo(string scenario) : base("/socket", false)
            {
                _scenario = scenario;
                if (scenario == "oversize") MaxMessageSize = 4;
            }
            public ConcurrentQueue<string> Messages { get; } = new();
            public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Dispatch(IWebSocketContext context, byte[] bytes) => base.OnMessageReceivedAsync(context, bytes, new Result(bytes.Length));
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Disconnected.TrySetResult(true);
                return Task.CompletedTask;
            }
            protected override async Task OnTextMessageReceivedAsync(IWebSocketContext context, string text)
            {
                if (_scenario == "failure" && text == "fail") throw new InvalidOperationException("deliberate callback failure");
                if (_scenario == "decoder-failure") throw new DecoderFallbackException("deliberate application decoder error");
                if (_scenario == "away" && text == "away")
                {
                    await context.WebSocket.CloseAsync(CloseStatusCode.Away, "away", context.CancellationToken);
                    return;
                }
                Messages.Enqueue(text);
                if (_scenario == "gate" && text == "hold")
                {
                    Entered.TrySetResult(true);
                    try { await Release.Task.WaitAsync(context.CancellationToken); }
                    finally { CallbackFinished.TrySetResult(true); }
                }
                await SendAsync(context, text);
            }
            protected override Task OnBinaryMessageReceivedAsync(IWebSocketContext context, byte[] data)
            {
                if (_scenario == "unsupported") return base.OnBinaryMessageReceivedAsync(context, data);
                Messages.Enqueue(Convert.ToBase64String(data));
                return SendAsync(context, data);
            }
        }
    }
}
