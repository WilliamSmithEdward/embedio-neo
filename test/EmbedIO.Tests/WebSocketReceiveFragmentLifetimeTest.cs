using System;
using System.Linq;
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
    public class WebSocketReceiveFragmentLifetimeTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Controlled : WebSocketModule
        {
            internal readonly TaskCompletionSource<bool> InitializeFirst = Signal();
            internal readonly TaskCompletionSource<bool> Disconnected = Signal();
            internal readonly TaskCompletionSource<bool>[] Started = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
            internal readonly TaskCompletionSource<bool>[] Finish = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
            internal readonly byte[][] Payloads = new byte[4][];
            internal int Calls;
            private int _connections;

            internal Controlled(bool pauseFirst = false) : base("/ws", false)
            {
                ReceiveLimits = new WebSocketReceiveLimits(2, 4, 2, 4);
                if (!pauseFirst) InitializeFirst.SetResult(true);
            }

            protected override Task OnClientConnectedAsync(IWebSocketContext context)
                => Interlocked.Increment(ref _connections) == 1 ? InitializeFirst.Task : Task.CompletedTask;

            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Disconnected.TrySetResult(true);
                return Task.CompletedTask;
            }

            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                var index = Interlocked.Increment(ref Calls) - 1;
                if (index >= Payloads.Length) throw new AssertionException("Unexpected delivery count.");
                Payloads[index] = buffer;
                Started[index].TrySetResult(true);
                return Finish[index].Task;
            }

            internal void ReleaseAll()
            {
                InitializeFirst.TrySetResult(true);
                foreach (var finish in Finish) finish.TrySetResult(true);
            }
        }

        private static (long Messages, long Bytes) Outstanding(WebSocketModule module)
        {
            var budget = typeof(WebSocketModule).GetProperty("DeliveryBudget", Hidden)?.GetValue(module)
                ?? throw new AssertionException("Missing configured budget.");
            var state = budget.GetType().GetProperty("Outstanding", Hidden)?.GetValue(budget)
                ?? throw new AssertionException("Missing budget snapshot.");
            return ((long)(state.GetType().GetProperty("Messages", Hidden)?.GetValue(state)
                    ?? throw new AssertionException("Missing message count.")),
                (long)(state.GetType().GetProperty("Bytes", Hidden)?.GetValue(state)
                    ?? throw new AssertionException("Missing payload bytes.")));
        }

        private static async Task SettlesAsync(WebSocketModule module, long messages, long bytes, CancellationToken token)
        {
            while (Outstanding(module) != (messages, bytes)) await Task.Delay(1, token);
            Assert.That(Outstanding(module), Is.EqualTo((messages, bytes)));
        }

        private static async Task PeerCloseAsync(ManagedWebSocketHardeningTest.RawClient client, CancellationToken token)
        {
            await client.SendAsync(0x88, new byte[] { 3, 232 }, token);
            var close = await client.ReadFrameAsync(token);
            Assert.That(close.Flags & 15, Is.EqualTo(8));
            Assert.That(ManagedWebSocketHardeningTest.RawClient.CloseCode(close.Payload), Is.EqualTo(1000));
        }

        private static async Task ServerAsync(Controlled module, Func<string, CancellationToken, Task> body)
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
                module.ReleaseAll();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PartialFragmentRetiresWithoutReleasingAnUnfinishedCallback(bool peerClose)
        {
            var module = new Controlled();
            await ServerAsync(module, async (url, token) =>
            {
                using var first = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await first.SendAsync(0x82, new byte[] { 42 }, token);
                await module.Started[0].Task.WaitAsync(token);
                await first.SendAsync(0x02, new byte[] { 7, 8, 9 }, token);
                await SettlesAsync(module, 2, 4, token);
                Assert.That(module.Calls, Is.EqualTo(1), "The unfinished fragment is not delivered.");
                Assert.That(module.Finish[0].Task.IsCompleted, Is.False);
                if (peerClose) await PeerCloseAsync(first, token);
                else first.Dispose();
                await module.Disconnected.Task.WaitAsync(token);
                await SettlesAsync(module, 1, 1, token);
                Assert.That(module.Finish[0].Task.IsCompleted, Is.False);
                Assert.That(module.Payloads[0], Is.EqualTo(new byte[] { 42 }));

                using var next = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await next.SendAsync(0x82, new byte[] { 43 }, token);
                await module.Started[1].Task.WaitAsync(token);
                Assert.That(module.Calls, Is.EqualTo(2));
                Assert.That(Outstanding(module), Is.EqualTo((2L, 2L)));
                Assert.That(module.Finish[0].Task.IsCompleted, Is.False, "Reconnect preserves old callback ownership.");
                module.Finish[1].SetResult(true);
                await SettlesAsync(module, 1, 1, token);
                module.Finish[0].SetResult(true);
                await SettlesAsync(module, 0, 0, token);
                Assert.That(module.Payloads[0], Is.EqualTo(new byte[] { 42 }), "Application-retained payload stays intact.");
                await PeerCloseAsync(next, token);
            });
        }

        [Test]
        public async Task CanceledInitializationDiscardsQueuedDeliveryBeforeFreshAcceptance()
        {
            var module = new Controlled(pauseFirst: true);
            await ServerAsync(module, async (url, token) =>
            {
                using var first = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await first.SendAsync(0x82, new byte[] { 1, 2, 3, 4 }, token);
                await SettlesAsync(module, 1, 4, token);
                Assert.That(module.Calls, Is.Zero);
                await first.SendAsync(0x82, new byte[] { 5 }, token);
                var close = await first.ReadFrameAsync(token);
                Assert.That(close.Flags & 15, Is.EqualTo(8));
                Assert.That(ManagedWebSocketHardeningTest.RawClient.CloseCode(close.Payload), Is.EqualTo(1008));
                await first.SendAsync(0x88, new byte[] { 3, 240 }, token);
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                module.InitializeFirst.SetCanceled(canceled.Token);
                await module.Disconnected.Task.WaitAsync(token);
                await SettlesAsync(module, 0, 0, token);
                Assert.That(module.Calls, Is.Zero, "Canceled initialization never invokes a data callback.");

                using var next = await ManagedWebSocketHardeningTest.RawClient.ConnectAsync(url, token);
                await next.SendAsync(0x82, new byte[] { 6 }, token);
                await module.Started[0].Task.WaitAsync(token);
                Assert.That(module.Payloads[0], Is.EqualTo(new byte[] { 6 }));
                Assert.That(Outstanding(module), Is.EqualTo((1L, 1L)));
                module.Finish[0].SetResult(true);
                await SettlesAsync(module, 0, 0, token);
                await PeerCloseAsync(next, token);
            });
        }
    }
}
