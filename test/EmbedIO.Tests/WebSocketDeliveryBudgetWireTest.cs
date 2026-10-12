using System;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class WebSocketDeliveryBudgetWireTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class Controlled : WebSocketModule
        {
            internal readonly TaskCompletionSource<bool> Initialize = Signal();
            internal readonly TaskCompletionSource<bool> Disconnected = Signal();
            internal readonly TaskCompletionSource<bool>[] Started = Enumerable.Range(0, 8).Select(_ => Signal()).ToArray();
            internal readonly TaskCompletionSource<bool>[] Finish = Enumerable.Range(0, 8).Select(_ => Signal()).ToArray();
            internal readonly byte[][] Payloads = new byte[8][];
            private readonly bool _failInitialization;
            internal IWebSocketContext? Context;
            internal int Calls;

            internal Controlled(int? count, long? bytes, int? moduleCount, long? moduleBytes, bool pause = false, bool fail = false, int maximum = 0)
                : base("/ws", false)
            {
                MaxMessageSize = maximum;
                _failInitialization = fail;
                ReceiveLimits = new WebSocketReceiveLimits(count, bytes, moduleCount, moduleBytes);
                if (!pause) Initialize.SetResult(true);
            }

            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Context = context;
                await Initialize.Task;
                if (_failInitialization) throw new InvalidOperationException("first-party initialization failure");
            }

            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                var index = Interlocked.Increment(ref Calls) - 1;
                if (index >= Payloads.Length) throw new AssertionException("Unexpected callback count.");
                Payloads[index] = buffer;
                Started[index].TrySetResult(true);
                return Finish[index].Task;
            }

            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            { Disconnected.TrySetResult(true); return Task.CompletedTask; }

            internal void ReleaseAll()
            {
                Initialize.TrySetResult(true);
                foreach (var finish in Finish) finish.TrySetResult(true);
            }
            internal WebSocketReceiveLimits? ConfiguredLimits => ReceiveLimits;
            internal void ConfigureLimits(WebSocketReceiveLimits? value) => ReceiveLimits = value;
        }

        private sealed class Sequential : WebSocketMessageModule
        {
            internal readonly TaskCompletionSource<bool> First = Signal();
            internal readonly TaskCompletionSource<bool> Second = Signal();
            internal readonly TaskCompletionSource<bool> ReleaseFirst = Signal();
            internal readonly TaskCompletionSource<bool> ReleaseSecond = Signal();
            private int _calls;
            internal Sequential() : base("/ws", false) => ReceiveLimits = new WebSocketReceiveLimits(2, 2, 2, 2);
            protected override Task OnBinaryMessageReceivedAsync(IWebSocketContext context, byte[] data)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    Assert.That(data, Is.EqualTo(new byte[] { 1 }));
                    First.SetResult(true); return ReleaseFirst.Task;
                }
                Assert.That(data, Is.EqualTo(new byte[] { 2 }));
                Second.SetResult(true); return ReleaseSecond.Task;
            }
        }


        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Test]
        public void FrameReaderRetirementDoesNotDisposeBorrowedInput()
        {
            using var input = new MemoryStream(new byte[] { 42 });
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketFrameStream")
                ?? throw new AssertionException("Missing frame reader.");
            using var reader = Activator.CreateInstance(type, new object[] { input, false }) as IDisposable
                ?? throw new AssertionException("Missing disposable reader.");
            reader.Dispose();
            reader.Dispose();
            Assert.That(input.CanRead, Is.True);
            Assert.That(input.ReadByte(), Is.EqualTo(42));
        }
        private static object? Budget(WebSocketModule module)
            => typeof(WebSocketModule).GetProperty("DeliveryBudget", Flags)?.GetValue(module);
        private static (long Messages, long Bytes) Outstanding(WebSocketModule module)
        {
            var budget = Budget(module) ?? throw new AssertionException("Missing configured budget.");
            var state = budget.GetType().GetProperty("Outstanding", Flags)?.GetValue(budget) ?? throw new AssertionException("Missing snapshot.");
            return ((long)(state.GetType().GetProperty("Messages", Flags)?.GetValue(state) ?? throw new AssertionException("Missing count.")),
                (long)(state.GetType().GetProperty("Bytes", Flags)?.GetValue(state) ?? throw new AssertionException("Missing bytes.")));
        }
        private static async Task SettlesAsync(WebSocketModule module, long count, long bytes, CancellationToken token)
        {
            while (Outstanding(module) != (count, bytes)) await Task.Delay(1, token);
            Assert.That(Outstanding(module), Is.EqualTo((count, bytes)));
        }
        private static async Task PolicyCloseAsync(ManagedWebSocketHardeningTest.RawClient client, int code, CancellationToken token)
        {
            var frame = await client.ReadFrameAsync(token);
            Assert.That(frame.Flags & 15, Is.EqualTo(8));
            Assert.That(ManagedWebSocketHardeningTest.RawClient.CloseCode(frame.Payload), Is.EqualTo(code));
            await client.SendAsync(0x88, new byte[] { (byte)(code >> 8), (byte)code }, token);
        }
        private static async Task PeerCloseAsync(ManagedWebSocketHardeningTest.RawClient client, CancellationToken token)
        {
            await client.SendAsync(0x88, new byte[] { 3, 232 }, token);
            var frame = await client.ReadFrameAsync(token);
            Assert.That(frame.Flags & 15, Is.EqualTo(8));
        }
        private static Task ServerAsync(Controlled module, Func<string, CancellationToken, Task> body)
            => ServerAsync(module, body, module.ReleaseAll);
        private static async Task ServerAsync(WebSocketModule module, Func<string, CancellationToken, Task> body, Action release)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(module).WithModule(new ActionModule("/", HttpVerbs.Get,
                    context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = server.RunAsync(stop.Token);
            try
            {
                await body(url, stop.Token);
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                Assert.That(await http.GetStringAsync(url + "healthy", stop.Token), Is.EqualTo("healthy"));
            }
            finally
            {
                release();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task HeldCallbacksKeepCountAndBytesAfterDequeueAndStillOverlap()
        {
            var module = new Controlled(2, 5, 2, 5);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x82, new byte[] { 1, 2 }, token);
                await module.Started[0].Task.WaitAsync(token);
                await client.SendAsync(0x82, new byte[] { 3, 4, 5 }, token);
                await module.Started[1].Task.WaitAsync(token);
                Assert.That(module.Finish[0].Task.IsCompleted, Is.False);
                Assert.That(Outstanding(module), Is.EqualTo((2L, 5L)));
                await client.SendAsync(0x82, Array.Empty<byte>(), token);
                await PolicyCloseAsync(client, 1008, token);
                Assert.That(module.Calls, Is.EqualTo(2));
                module.Finish[1].SetResult(true);
                await SettlesAsync(module, 1, 2, token);
                Assert.That(module.Payloads[0], Is.EqualTo(new byte[] { 1, 2 }));
                module.Finish[0].SetResult(true);
                await SettlesAsync(module, 0, 0, token);
            });
        }

        [Test]
        public async Task EarlyAdmittedMessageSurvivesOverloadAndPeerCloseDuringInitialization()
        {
            var module = new Controlled(1, 4, 1, 4, pause: true);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x82, new byte[] { 7, 8 }, token);
                await SettlesAsync(module, 1, 2, token);
                Assert.That(module.Calls, Is.Zero);
                await client.SendAsync(0x82, new byte[] { 9 }, token);
                await PolicyCloseAsync(client, 1008, token);
                module.Initialize.SetResult(true);
                await module.Started[0].Task.WaitAsync(token);
                Assert.That(module.Payloads[0], Is.EqualTo(new byte[] { 7, 8 }));
                Assert.That(module.Calls, Is.EqualTo(1));
                module.Finish[0].SetResult(true);
                await SettlesAsync(module, 0, 0, token);
            });
        }

        [Test]
        public async Task FailedInitializationDiscardsUndeliveredOwnership()
        {
            var module = new Controlled(2, 10, 2, 10, pause: true, fail: true);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x82, new byte[] { 1, 2, 3 }, token);
                await SettlesAsync(module, 1, 3, token);
                module.Initialize.SetResult(true);
                var close = await client.ReadFrameAsync(token);
                Assert.That(close.Flags & 15, Is.EqualTo(8));
                await client.SendAsync(0x88, new byte[] { 3, 232 }, token);
                await SettlesAsync(module, 0, 0, token);
                Assert.That(module.Calls, Is.Zero);
            });
        }

        [Test]
        public async Task FragmentBytesReserveBeforeGrowthWhilePingStillResponds()
        {
            var module = new Controlled(2, 3, 2, 3);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x02, new byte[] { 1, 2 }, token);
                await SettlesAsync(module, 1, 2, token);
                await client.SendAsync(0x89, new byte[] { 42 }, token);
                var pong = await client.ReadFrameAsync(token);
                Assert.That(pong.Flags & 15, Is.EqualTo(10));
                Assert.That(pong.Payload, Is.EqualTo(new byte[] { 42 }));
                await client.SendAsync(0x80, new byte[] { 3, 4 }, token);
                await PolicyCloseAsync(client, 1008, token);
                await SettlesAsync(module, 0, 0, token);
                Assert.That(module.Calls, Is.Zero);
            });
        }

        [Test]
        public async Task EmptyMessagesConsumeOutstandingCountWithNoPayloadBytes()
        {
            var module = new Controlled(1, 0, 1, 0);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x82, Array.Empty<byte>(), token);
                await module.Started[0].Task.WaitAsync(token);
                Assert.That(Outstanding(module), Is.EqualTo((1L, 0L)));
                await client.SendAsync(0x82, Array.Empty<byte>(), token);
                await PolicyCloseAsync(client, 1008, token);
                Assert.That(module.Calls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task ReconnectCannotResetModuleBudgetWhileOldCallbackLives()
        {
            var module = new Controlled(2, 8, 1, 8);
            await ServerAsync(module, async (url, token) =>
            {
                using var old = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await old.SendAsync(0x82, new byte[] { 5, 6 }, token);
                await module.Started[0].Task.WaitAsync(token);
                await PeerCloseAsync(old, token);
                await module.Disconnected.Task.WaitAsync(token);
                Assert.That(Outstanding(module), Is.EqualTo((1L, 2L)));
                using var refused = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await refused.SendAsync(0x82, new byte[] { 9 }, token);
                await PolicyCloseAsync(refused, 1008, token);
                Assert.That(module.Calls, Is.EqualTo(1));
                module.Finish[0].SetResult(true);
                await SettlesAsync(module, 0, 0, token);
                using var next = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await next.SendAsync(0x82, new byte[] { 7 }, token);
                await module.Started[1].Task.WaitAsync(token);
                Assert.That(module.Payloads[0], Is.EqualTo(new byte[] { 5, 6 }));
                module.Finish[1].SetResult(true);
                await SettlesAsync(module, 0, 0, token);
                await PeerCloseAsync(next, token);
            });
        }

        [Test]
        public async Task ExistingSingleMessageLimitStillUses1009()
        {
            var module = new Controlled(4, 100, 4, 100, maximum: 2);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x82, new byte[] { 1, 2, 3 }, token);
                await PolicyCloseAsync(client, 1009, token);
                Assert.That(module.Calls, Is.Zero);
                Assert.That(Outstanding(module), Is.EqualTo((0L, 0L)));
            });
        }

        [Test]
        public async Task FourNullLimitsCreateNoBudgetOwnership()
        {
            var module = new Controlled(null, null, null, null);
            Assert.That(Budget(module), Is.Null);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x82, new byte[] { 1 }, token);
                await module.Started[0].Task.WaitAsync(token);
                Assert.That(Budget(module), Is.Null);
                var socket = module.Context?.WebSocket ?? throw new AssertionException("Missing live socket.");
                var field = socket.GetType().GetField("_deliveryBudget", Flags) ?? throw new AssertionException("Missing socket budget capability.");
                Assert.That(field.GetValue(socket), Is.Null);
                await PeerCloseAsync(client, token);
            });
        }

        [Test]
        public async Task SequentialModuleTailStaysChargedUntilItsActualCallbackCompletes()
        {
            var module = new Sequential();
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x82, new byte[] { 1 }, token);
                await module.First.Task.WaitAsync(token);
                await client.SendAsync(0x82, new byte[] { 2 }, token);
                await SettlesAsync(module, 2, 2, token);
                Assert.That(module.Second.Task.IsCompleted, Is.False);
                module.ReleaseFirst.SetResult(true);
                await module.Second.Task.WaitAsync(token);
                await SettlesAsync(module, 1, 1, token);
                module.ReleaseSecond.SetResult(true);
                await SettlesAsync(module, 0, 0, token);
                await PeerCloseAsync(client, token);
            }, () => { module.ReleaseFirst.TrySetResult(true); module.ReleaseSecond.TrySetResult(true); });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CallbackFaultOrCancellationSettlesBudgetAndKeepsLegacySocketUsable(bool cancelled)
        {
            var module = new Controlled(1, 4, 1, 4);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x82, new byte[] { 1 }, token);
                await module.Started[0].Task.WaitAsync(token);
                if (cancelled) module.Finish[0].SetCanceled();
                else module.Finish[0].SetException(new InvalidOperationException("first-party callback marker"));
                await SettlesAsync(module, 0, 0, token);
                await client.SendAsync(0x82, new byte[] { 2 }, token);
                await module.Started[1].Task.WaitAsync(token);
                Assert.That(module.Payloads[0], Is.EqualTo(new byte[] { 1 }));
                module.Finish[1].SetResult(true);
                await SettlesAsync(module, 0, 0, token);
                await PeerCloseAsync(client, token);
            });
        }

        [Test]
        public async Task ExcessAnnouncedPayloadClosesBeforeWaitingForItsMissingBytes()
        {
            var module = new Controlled(1, 4, 1, 4);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.Stream.WriteAsync(ManagedWebSocketHardeningTest.RawClient.Frame(0x82, Array.Empty<byte>(), announced: 70000), token);
                var close = await client.ReadFrameAsync(token);
                Assert.That(close.Flags & 15, Is.EqualTo(8));
                Assert.That(ManagedWebSocketHardeningTest.RawClient.CloseCode(close.Payload), Is.EqualTo(1008));
                Assert.That(module.Calls, Is.Zero);
                Assert.That(Outstanding(module), Is.EqualTo((0L, 0L)));
                // Close timeout bounds missing rejected payload; this client owns
                // teardown instead of placing an ACK inside that unread payload.
            });
        }

        [Test]
        public async Task InvalidUtf8DiscardsItsUndeliveredReservation()
        {
            var module = new Controlled(1, 4, 1, 4);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.SendAsync(0x81, new byte[] { 0xc3, 0x28 }, token);
                await PolicyCloseAsync(client, 1007, token);
                await SettlesAsync(module, 0, 0, token);
                Assert.That(module.Calls, Is.Zero);
            });
        }

        [Test]
        public async Task TruncatedPayloadReleasesOnlyWhenTheOwnedReceiveSettles()
        {
            var module = new Controlled(1, 4, 1, 4);
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await client.Stream.WriteAsync(ManagedWebSocketHardeningTest.RawClient.Frame(0x82, new byte[] { 1 }, announced: 3), token);
                await SettlesAsync(module, 1, 3, token);
                Assert.That(module.Calls, Is.Zero);
                client.Dispose();
                await SettlesAsync(module, 0, 0, token);
            });
        }

        [Test]
        public async Task ProtectedConfigurationLocksBeforeLiveAcceptanceAndKeepsItsSnapshot()
        {
            var module = new Controlled(1, 4, 1, 4);
            var chosen = module.ConfiguredLimits;
            await ServerAsync(module, async (url, token) =>
            {
                using var client = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                Assert.Throws<InvalidOperationException>(() => module.ConfigureLimits(new WebSocketReceiveLimits(2, 8, 2, 8)));
                Assert.That(module.ConfiguredLimits, Is.SameAs(chosen));
                await client.SendAsync(0x82, new byte[] { 1 }, token);
                await module.Started[0].Task.WaitAsync(token);
                await client.SendAsync(0x82, new byte[] { 2 }, token);
                await PolicyCloseAsync(client, 1008, token);
                Assert.That(module.Calls, Is.EqualTo(1));
            });
        }
    }
}
