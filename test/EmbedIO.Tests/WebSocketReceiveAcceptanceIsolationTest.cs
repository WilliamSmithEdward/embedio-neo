using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class WebSocketReceiveAcceptanceIsolationTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class ScopeModule : WebSocketModule
        {
            internal readonly TaskCompletionSource<bool> Connected = Signal();
            internal IWebSocket? Accepted;

            internal ScopeModule(int? count) : base("/ws", false)
            {
                if (count.HasValue) ReceiveLimits = new WebSocketReceiveLimits(count, 8, count, 8);
            }

            internal Task Dispatch(IHttpContext context) => OnRequestAsync(context);

            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Accepted = context.WebSocket;
                // A constructor in application initialization must not inherit the
                // reservation consumed by the accepted transport constructor.
                using var unrelated = new SocketOwner();
                Assert.That(SocketBudget(unrelated.Socket), Is.Null);
                Connected.TrySetResult(true);
                return Task.CompletedTask;
            }

            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => throw new AssertionException("The controlled acceptance adapter sends no application frames.");
        }

        private sealed class SocketOwner : IDisposable
        {
            private readonly MemoryStream _stream = new();
            internal IWebSocket Socket { get; }

            internal SocketOwner()
            {
                var type = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocket")
                    ?? throw new AssertionException("Missing managed socket.");
                Socket = Activator.CreateInstance(type, Hidden, null, new object[] { _stream, (Action)(() => { }) }, null) as IWebSocket
                    ?? throw new AssertionException("Missing managed socket constructor.");
            }

            public void Dispose()
            {
                try
                {
                    // The adapter has no receiving peer. Use the existing internal
                    // no-wire close path to settle its actual module lifetime.
                    var close = Socket.GetType().GetMethod("InternalCloseAsync", Hidden)
                        ?? throw new AssertionException("Missing controlled close path.");
                    (close.Invoke(Socket, new object?[] { null, false, false, CancellationToken.None }) as Task
                        ?? throw new AssertionException("Missing close operation.")).GetAwaiter().GetResult();
                    Socket.Dispose();
                }
                finally { _stream.Dispose(); }
            }
        }

        public class RequestProxy : DispatchProxy
        {
            private readonly NameValueCollection _headers = new();
            protected override object? Invoke(MethodInfo? method, object?[]? arguments)
                => method?.Name == "get_Headers" ? _headers
                    : throw new AssertionException("Unexpected request access: " + method?.Name);
        }

        public class HttpProxy : DispatchProxy
        {
            public Func<Task<IWebSocketContext>>? Accept { get; set; }
            public CancellationToken Token { get; set; }
            private readonly IHttpRequest _request = DispatchProxy.Create<IHttpRequest, RequestProxy>();

            protected override object? Invoke(MethodInfo? method, object?[]? arguments)
                => method?.Name switch
                {
                    "get_RequestedPath" => "/",
                    "get_Request" => _request,
                    "get_CancellationToken" => Token,
                    "AcceptWebSocketAsync" => (Accept ?? throw new AssertionException("Missing accept adapter."))(),
                    _ => throw new AssertionException("Unexpected HTTP context access: " + method?.Name),
                };
        }

        public class SocketProxy : DispatchProxy
        {
            public IWebSocket? Socket { get; set; }
            public CancellationToken Token { get; set; }
            private readonly string _id = Guid.NewGuid().ToString("N");
            protected override object? Invoke(MethodInfo? method, object?[]? arguments)
                => method?.Name switch
                {
                    "get_Id" => _id,
                    "get_WebSocket" => Socket,
                    "get_CancellationToken" => Token,
                    _ => throw new AssertionException("Unexpected socket context access: " + method?.Name),
                };
        }

        private static IHttpContext Context(Func<Task<IWebSocketContext>> accept, CancellationToken token)
        {
            var context = DispatchProxy.Create<IHttpContextImpl, HttpProxy>();
            var proxy = (HttpProxy)(object)context;
            proxy.Accept = accept;
            proxy.Token = token;
            return context;
        }

        private static IWebSocketContext SocketContext(IWebSocket socket, CancellationToken token)
        {
            var context = DispatchProxy.Create<IWebSocketContext, SocketProxy>();
            var proxy = (SocketProxy)(object)context;
            proxy.Socket = socket;
            proxy.Token = token;
            return context;
        }

        private static object? SocketBudget(IWebSocket socket)
            => (socket.GetType().GetField("_deliveryBudget", Hidden)
                ?? throw new AssertionException("Missing socket delivery budget.")).GetValue(socket);

        private static object? ModuleBudget(WebSocketModule module)
            => (typeof(WebSocketModule).GetProperty("DeliveryBudget", Hidden)
                ?? throw new AssertionException("Missing module budget.")).GetValue(module);

        private static void AssertOwner(ScopeModule module, IWebSocket socket)
        {
            var connection = SocketBudget(socket);
            var moduleBudget = ModuleBudget(module);
            if (moduleBudget == null) Assert.That(connection, Is.Null);
            else
            {
                Assert.That(connection, Is.Not.Null);
                var actual = connection ?? throw new AssertionException("Missing connection budget.");
                Assert.That((actual.GetType().GetField("_owner", Hidden)
                    ?? throw new AssertionException("Missing connection owner.")).GetValue(actual), Is.SameAs(moduleBudget));
            }
        }

        private sealed class NestedModule : WebSocketModule
        {
            internal readonly TaskCompletionSource<bool> Connected = Signal();
            internal IWebSocket? Accepted;
            internal object? CallbackConstructorBudget;

            internal NestedModule(int? count) : base("/ws", false)
            {
                if (count.HasValue) ReceiveLimits = new WebSocketReceiveLimits(count, 8, count, 8);
            }

            internal Task Dispatch(IHttpContext context) => OnRequestAsync(context);

            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Accepted = context.WebSocket;
                // Record separately: restoring an outer acceptance scope could
                // leak its pending reservation into application initialization.
                using (var unrelated = new SocketOwner())
                    CallbackConstructorBudget = SocketBudget(unrelated.Socket);
                Connected.TrySetResult(true);
                return Task.CompletedTask;
            }

            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => throw new AssertionException("The nested acceptance adapter sends no application frames.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NestedAcceptanceKeepsUnlimitedOrConfiguredInnerOwnershipAndRestoresOuter(bool configuredInner)
        {
            using var outerModule = new NestedModule(1);
            using var innerModule = new NestedModule(configuredInner ? 3 : null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            SocketOwner? outerOwner = null;
            SocketOwner? innerOwner = null;
            Task innerOperation = Task.CompletedTask;
            var outerOperation = outerModule.Dispatch(Context(async () =>
            {
                // Nest before the outer constructor consumes its configured
                // AsyncLocal connection. Both adapters cross a real async boundary.
                await Task.Yield();
                innerOperation = innerModule.Dispatch(Context(async () =>
                {
                    await Task.Yield();
                    var owner = new SocketOwner();
                    innerOwner = owner;
                    return SocketContext(owner.Socket, timeout.Token);
                }, timeout.Token));
                await innerModule.Connected.Task.WaitAsync(timeout.Token);
                var outer = new SocketOwner();
                outerOwner = outer;
                return SocketContext(outer.Socket, timeout.Token);
            }, timeout.Token));
            try
            {
                await outerModule.Connected.Task.WaitAsync(timeout.Token);
                var innerSocket = innerModule.Accepted ?? throw new AssertionException("Missing nested socket.");
                var outerSocket = outerModule.Accepted ?? throw new AssertionException("Missing outer socket.");
                var innerConnection = SocketBudget(innerSocket);
                var outerConnection = SocketBudget(outerSocket)
                    ?? throw new AssertionException("Outer configured acceptance lost its budget.");
                string Ledger(object? connection)
                {
                    if (connection == null) return "none";
                    var owner = (connection.GetType().GetField("_owner", Hidden)
                        ?? throw new AssertionException("Missing recorded ledger owner.")).GetValue(connection);
                    return ReferenceEquals(owner, ModuleBudget(outerModule)) ? "outer"
                        : ReferenceEquals(owner, ModuleBudget(innerModule)) ? "inner" : "unexpected";
                }
                TestContext.Out.WriteLine($"Nested acceptance: configuredInner={configuredInner}, innerConstructor={Ledger(innerConnection)}, "
                    + $"innerCallbackConstructor={Ledger(innerModule.CallbackConstructorBudget)}, outerConstructor={Ledger(outerConnection)}, "
                    + $"outerCallbackConstructor={Ledger(outerModule.CallbackConstructorBudget)}");
                Assert.Multiple(() =>
                {
                    Assert.That((outerConnection.GetType().GetField("_owner", Hidden)
                        ?? throw new AssertionException("Missing outer ledger owner.")).GetValue(outerConnection),
                        Is.SameAs(ModuleBudget(outerModule)));
                    if (configuredInner)
                    {
                        var configured = innerConnection ?? throw new AssertionException("Inner configured acceptance lost its budget.");
                        Assert.That((configured.GetType().GetField("_owner", Hidden)
                            ?? throw new AssertionException("Missing inner ledger owner.")).GetValue(configured),
                            Is.SameAs(ModuleBudget(innerModule)));
                        Assert.That(configured, Is.Not.SameAs(outerConnection));
                    }
                    else Assert.That(innerConnection, Is.Null,
                        "An unlimited nested accept must not consume the pending outer module's connection reservation.");
                    Assert.That(innerModule.CallbackConstructorBudget, Is.Null,
                        "A callback-created unrelated socket must not consume the pending outer acceptance budget.");
                    Assert.That(outerModule.CallbackConstructorBudget, Is.Null,
                        "Outer initialization must not leak its accepted connection's reservation.");
                });
                using var later = new SocketOwner();
                Assert.That(SocketBudget(later.Socket), Is.Null);
            }
            finally
            {
                innerOwner?.Dispose();
                outerOwner?.Dispose();
                timeout.Cancel();
                await Task.WhenAll(innerOperation, outerOperation).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task ConcurrentConfiguredAndUnlimitedAcceptsKeepTheirOwnAsyncScopes()
        {
            using var first = new ScopeModule(1);
            using var second = new ScopeModule(3);
            using var unlimited = new ScopeModule(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var release = Signal();
            var modules = new[] { first, second, unlimited };
            var entered = new[] { Signal(), Signal(), Signal() };
            var owners = new SocketOwner?[3];
            var operations = new Task[3];
            Array.Fill(operations, Task.CompletedTask);
            try
            {
                for (var i = 0; i < modules.Length; i++)
                {
                    var index = i;
                    operations[index] = modules[index].Dispatch(Context(async () =>
                    {
                        entered[index].TrySetResult(true);
                        await release.Task.WaitAsync(timeout.Token);
                        var owner = new SocketOwner();
                        owners[index] = owner;
                        return SocketContext(owner.Socket, timeout.Token);
                    }, timeout.Token));
                }
                await Task.WhenAll(Array.ConvertAll(entered, signal => signal.Task)).WaitAsync(timeout.Token);
                Assert.That(Array.TrueForAll(operations, operation => !operation.IsCompleted), Is.True);
                release.SetResult(true);
                await Task.WhenAll(Array.ConvertAll(modules, module => module.Connected.Task)).WaitAsync(timeout.Token);
                for (var i = 0; i < modules.Length; i++)
                    AssertOwner(modules[i], modules[i].Accepted ?? throw new AssertionException("Missing accepted socket."));
                Assert.That(ModuleBudget(first), Is.Not.SameAs(ModuleBudget(second)));
                using var later = new SocketOwner();
                Assert.That(SocketBudget(later.Socket), Is.Null);
            }
            finally
            {
                release.TrySetResult(true);
                foreach (var owner in owners) owner?.Dispose();
                timeout.Cancel();
                await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedOrCanceledAcceptDoesNotChargeOrConfigureLaterSockets(bool canceled)
        {
            using var limited = new ScopeModule(1);
            using var unlimited = new ScopeModule(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var failureToken = new CancellationTokenSource();
            var entered = Signal();
            var release = Signal();
            var failed = limited.Dispatch(Context(async () =>
            {
                entered.SetResult(true);
                await release.Task.WaitAsync(timeout.Token);
                if (canceled)
                {
                    failureToken.Cancel();
                    return await Task.FromCanceled<IWebSocketContext>(failureToken.Token);
                }
                throw new IOException("controlled accept failure before socket construction");
            }, failureToken.Token));
            await entered.Task.WaitAsync(timeout.Token);
            SocketOwner? concurrentOwner = null;
            var unaffected = unlimited.Dispatch(Context(() =>
            {
                var owner = new SocketOwner();
                concurrentOwner = owner;
                return Task.FromResult(SocketContext(owner.Socket, timeout.Token));
            }, timeout.Token));
            try
            {
                await unlimited.Connected.Task.WaitAsync(timeout.Token);
                Assert.That(failed.IsCompleted, Is.False);
                AssertOwner(unlimited, unlimited.Accepted ?? throw new AssertionException("Missing concurrent ordinary socket."));
                release.SetResult(true);
                if (canceled)
                {
                    var error = await Assert.CatchAsync<OperationCanceledException>(async () => await failed)
                        ?? throw new AssertionException("Expected the canceled acceptance error.");
                    Assert.That(error.CancellationToken, Is.EqualTo(failureToken.Token));
                }
                else await Assert.ThrowsAsync<IOException>(async () => await failed);
                AssertOwner(unlimited, unlimited.Accepted ?? throw new AssertionException("Ordinary socket lost after failed acceptance."));
            }
            finally
            {
                release.TrySetResult(true);
                concurrentOwner?.Dispose();
                await unaffected.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.That(limited.Connected.Task.IsCompleted, Is.False);

            var budget = ModuleBudget(limited) ?? throw new AssertionException("Missing limited budget.");
            var snapshot = budget.GetType().GetProperty("Outstanding", Hidden)?.GetValue(budget)
                ?? throw new AssertionException("Missing outstanding snapshot.");
            Assert.That(snapshot.GetType().GetProperty("Messages", Hidden)?.GetValue(snapshot), Is.EqualTo(0L));
            Assert.That(snapshot.GetType().GetProperty("Bytes", Hidden)?.GetValue(snapshot), Is.EqualTo(0L));

            var acceptedOwners = new List<SocketOwner>();
            Task<IWebSocketContext> AcceptFresh()
            {
                var owner = new SocketOwner();
                acceptedOwners.Add(owner);
                return Task.FromResult(SocketContext(owner.Socket, timeout.Token));
            }
            var ordinary = unlimited.Dispatch(Context(AcceptFresh, timeout.Token));
            var configured = limited.Dispatch(Context(AcceptFresh, timeout.Token));
            try
            {
                await Task.WhenAll(unlimited.Connected.Task, limited.Connected.Task).WaitAsync(timeout.Token);
                AssertOwner(unlimited, unlimited.Accepted ?? throw new AssertionException("Missing ordinary socket."));
                AssertOwner(limited, limited.Accepted ?? throw new AssertionException("Missing configured socket."));
                using var later = new SocketOwner();
                Assert.That(SocketBudget(later.Socket), Is.Null);
            }
            finally
            {
                foreach (var owner in acceptedOwners) owner.Dispose();
                timeout.Cancel();
                await Task.WhenAll(ordinary, configured).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
