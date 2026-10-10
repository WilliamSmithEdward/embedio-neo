using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;
using HttpListenerException = System.Net.HttpListenerException;

namespace EmbedIO.Tests
{
    // Concurrency contracts of managed listener admission: registration from many
    // connections, accept, cancellation, stop, restart and graceful drain.
    public class ListenerAdmissionConcurrencyTest
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly Action<Net.HttpListener, IHttpContextImpl> Register =
            (typeof(Net.HttpListener).GetMethod("RegisterContext", PrivateInstance)
                ?? throw new AssertionException("Missing RegisterContext."))
            .CreateDelegate<Action<Net.HttpListener, IHttpContextImpl>>();

        [TestCase(false)]
        [TestCase(true)]
        public void UnregisteredContextsDoNotAccumulateWhileAcceptIsPaused(bool liveHead)
        {
            using var listener = new Net.HttpListener();
            listener.Start();
            if (liveHead) Register(listener, FakeContext.Create("live-head"));
            var unregister = (typeof(Net.HttpListener).GetMethod("UnregisterContext", PrivateInstance)
                ?? throw new AssertionException("Missing context unregistration."))
                .CreateDelegate<Action<Net.HttpListener, IHttpContextImpl>>();
            for (var index = 0; index < 10000; index++)
            {
                var context = FakeContext.Create("withdrawn:" + index);
                Register(listener, context);
                unregister(listener, context);
            }
            Assert.That(Pending(listener).Count, Is.EqualTo(liveHead ? 1 : 0));
            Assert.That(Order(listener), Is.LessThanOrEqualTo((liveHead ? 1 : 0) + 128),
                "Withdrawn queue references must remain bounded even when accept is paused.");
        }
        [Test]
        public async Task WithdrawnQueueCompactionDoesNotLoseLiveRegistrationsDuringAccept()
        {
            using var listener = new Net.HttpListener();
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var unregister = (typeof(Net.HttpListener).GetMethod("UnregisterContext", PrivateInstance)
                ?? throw new AssertionException("Missing context unregistration."))
                .CreateDelegate<Action<Net.HttpListener, IHttpContextImpl>>();
            const int producers = 4, perProducer = 1000;
            var accepted = new HashSet<string>();
            var consumer = Task.Run(async () =>
            {
                var live = 0;
                while (live < producers * perProducer)
                {
                    var context = await listener.GetContextAsync(timeout.Token);
                    Assert.That(accepted.Add(context.Id), Is.True, "Compaction duplicated a claim.");
                    if (context.Id.StartsWith("kept:", StringComparison.Ordinal)) live++;
                }
            });
            var registering = Enumerable.Range(0, producers).Select(producer => Task.Run(() =>
            {
                for (var index = 0; index < perProducer; index++)
                {
                    Register(listener, FakeContext.Create($"kept:{producer}:{index}"));
                    for (var withdrawn = 0; withdrawn < 5; withdrawn++)
                    {
                        var context = FakeContext.Create($"withdrawn:{producer}:{index}:{withdrawn}");
                        Register(listener, context);
                        unregister(listener, context);
                    }
                }
            }, timeout.Token)).ToArray();
            await Task.WhenAll(registering).WaitAsync(timeout.Token);
            await consumer.WaitAsync(timeout.Token);
            for (var producer = 0; producer < producers; producer++)
                for (var index = 0; index < perProducer; index++)
                    Assert.That(accepted, Does.Contain($"kept:{producer}:{index}"));
        }
        [Test]
        public async Task ConsumedQueueBurstDoesNotLeaveAnUnboundedWakeupBacklog()
        {
            using var listener = new Net.HttpListener();
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = DispatchProxy.Create<IHttpContextImpl, PausedClaimContext>();
            var paused = (PausedClaimContext)(object)first;
            var receiving = listener.GetContextAsync(timeout.Token);
            try
            {
                Register(listener, first);
                await paused.Claiming.Task.WaitAsync(timeout.Token);
                for (var index = 0; index < 1024; index++) Register(listener, FakeContext.Create("burst:" + index));
                paused.Resume.TrySetResult();
                Assert.That(await receiving.WaitAsync(timeout.Token), Is.SameAs(first));
                for (var index = 0; index < 1024; index++)
                    Assert.That((await listener.GetContextAsync(timeout.Token)).Id, Is.EqualTo("burst:" + index));
                var semaphore = (SemaphoreSlim)(typeof(Net.HttpListener).GetField("_ctxQueueSem", PrivateInstance)?.GetValue(listener)
                    ?? throw new AssertionException("Missing accept wakeup semaphore."));
                Assert.That(semaphore.CurrentCount, Is.LessThanOrEqualTo(1),
                    "Wakeups must not accumulate independently of pending work.");
            }
            finally { paused.Resume.TrySetResult(); await receiving.WaitAsync(TimeSpan.FromSeconds(2)); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CoalescedWakeupsDeliverBurstsToEveryWaitingAccept(bool restart)
        {
            using var listener = new Net.HttpListener();
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (var round = 0; round < 20; round++)
            {
                var accepts = Enumerable.Range(0, 32).Select(_ => listener.GetContextAsync(timeout.Token)).ToArray();
                if (restart)
                {
                    listener.Stop();
                    listener.Start();
                    foreach (var accept in accepts)
                        await Assert.ThatAsync(async () => await accept, Throws.TypeOf<HttpListenerException>());
                    accepts = Enumerable.Range(0, 32).Select(_ => listener.GetContextAsync(timeout.Token)).ToArray();
                }
                for (var index = 0; index < accepts.Length; index++)
                    Register(listener, FakeContext.Create($"{round}:{index}"));
                var delivered = await Task.WhenAll(accepts).WaitAsync(timeout.Token);
                Assert.That(delivered.Select(context => context.Id), Is.EquivalentTo(
                    Enumerable.Range(0, accepts.Length).Select(index => $"{round}:{index}")));
                Assert.That(Pending(listener).Count, Is.Zero);
            }
        }

        public class PausedClaimContext : DispatchProxy
        {
            private int _reads;
            internal readonly TaskCompletionSource Claiming = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod?.Name == "get_Id")
                {
                    if (Interlocked.Increment(ref _reads) == 2)
                    {
                        Claiming.TrySetResult();
                        if (!Resume.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Claim was not resumed.");
                    }
                    return "paused-first";
                }
                var type = targetMethod?.ReturnType;
                return type != null && type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
        }
        [Test]
        public async Task ConcurrentRegistrationsAreAcceptedOnceInEachProducersOrder()
        {
            const int producers = 8;
            const int perProducer = 2000;
            using var listener = new Net.HttpListener();
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var accepted = new List<IHttpContextImpl>(producers * perProducer);
            var consumer = Task.Run(async () =>
            {
                while (accepted.Count < producers * perProducer)
                    accepted.Add(await listener.GetContextAsync(timeout.Token));
            });
            using var start = new Barrier(producers);
            var registering = Enumerable.Range(0, producers).Select(producer => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait(timeout.Token);
                for (var index = 0; index < perProducer; index++) Register(listener, FakeContext.Create($"{producer}:{index}"));
            }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            await Task.WhenAll(registering).WaitAsync(timeout.Token);
            await consumer.WaitAsync(timeout.Token);

            var ids = accepted.Select(context => context.Id).ToArray();
            Assert.That(ids, Is.Unique);
            Assert.That(ids, Has.Length.EqualTo(producers * perProducer));
            for (var producer = 0; producer < producers; producer++)
            {
                // Each producer stands in for one connection: its requests must leave in arrival order.
                var order = ids.Where(id => id.StartsWith($"{producer}:", StringComparison.Ordinal))
                    .Select(id => int.Parse(id.Substring(id.IndexOf(':', StringComparison.Ordinal) + 1), System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
                Assert.That(order, Is.EqualTo(Enumerable.Range(0, perProducer)), $"Producer {producer} was reordered.");
            }
            Assert.That(Pending(listener).Count, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RegistrationsRacingStopOrDisposeNeverRemainQueued(bool dispose)
        {
            for (var iteration = 0; iteration < 25; iteration++)
            {
                var listener = new Net.HttpListener();
                try
                {
                    listener.Start();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var acceptedIds = new ConcurrentDictionary<string, byte>();
                    var registeredIds = new ConcurrentDictionary<string, byte>();
                    var consumer = Task.Run(async () =>
                    {
                        try
                        {
                            while (true)
                            {
                                var context = await listener.GetContextAsync(timeout.Token);
                                Assert.That(acceptedIds.TryAdd(context.Id, 0), Is.True, "A context was accepted twice.");
                            }
                        }
                        catch (HttpListenerException error) when (error.ErrorCode == 995) { }
                        catch (ObjectDisposedException) when (dispose) { }
                    });
                    var stopped = 0;
                    var producers = Enumerable.Range(0, 8).Select(producer => Task.Factory.StartNew(() =>
                    {
                        // Keep registering after Stop: every later attempt must be refused.
                        for (var index = 0; Volatile.Read(ref stopped) == 0 || index % 64 != 0; index++)
                        {
                            var context = FakeContext.Create($"{producer}:{index}");
                            var afterStop = Volatile.Read(ref stopped) == 2;
                            try
                            {
                                Register(listener, context);
                                Assert.That(afterStop, Is.False, "A registration that began after Stop returned succeeded.");
                                _ = registeredIds.TryAdd(context.Id, 0);
                            }
                            catch (HttpListenerException error) when (error.ErrorCode == 995) { }
                        }
                    }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

                    await Task.Delay(iteration % 5, timeout.Token);
                    Volatile.Write(ref stopped, 1);
                    if (dispose) listener.Dispose();
                    else listener.Stop();
                    Volatile.Write(ref stopped, 2);
                    var queue = Pending(listener);
                    Assert.That(queue.Count, Is.Zero, "Stop returned with a context still queued.");
                    await Task.WhenAll(producers).WaitAsync(timeout.Token);
                    await consumer.WaitAsync(timeout.Token);
                    Assert.That(queue.Count, Is.Zero, "A registration was queued after Stop returned.");
                    Assert.That(Order(listener), Is.Zero, "Stop retained references to queued contexts.");
                    Assert.That(acceptedIds.Keys.Except(registeredIds.Keys), Is.Empty);
                }
                finally { listener.Dispose(); }
            }
        }

        [Test]
        public async Task CanceledAcceptsDoNotLoseOrDuplicateConcurrentRegistrations()
        {
            const int producers = 4;
            const int perProducer = 1500;
            using var listener = new Net.HttpListener();
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var accepted = new ConcurrentDictionary<string, int>();
            var finished = 0;
            var cancelers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
            {
                for (var attempt = 0; Volatile.Read(ref finished) == 0; attempt++)
                {
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    if ((attempt + worker) % 3 == 0) cancel.Cancel();
                    else cancel.CancelAfter(TimeSpan.FromTicks(attempt % 2000));
                    try { _ = accepted.AddOrUpdate((await listener.GetContextAsync(cancel.Token)).Id, 1, (_, count) => count + 1); }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested && !timeout.IsCancellationRequested) { }
                }
            })).ToArray();
            var registering = Enumerable.Range(0, producers).Select(producer => Task.Factory.StartNew(() =>
            {
                for (var index = 0; index < perProducer; index++) Register(listener, FakeContext.Create($"{producer}:{index}"));
            }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            await Task.WhenAll(registering).WaitAsync(timeout.Token);
            Volatile.Write(ref finished, 1);
            await Task.WhenAll(cancelers).WaitAsync(timeout.Token);
            // Whatever the canceled accepts left behind must still be available to a plain accept.
            while (accepted.Count < producers * perProducer)
                _ = accepted.AddOrUpdate((await listener.GetContextAsync(timeout.Token)).Id, 1, (_, count) => count + 1);

            Assert.That(accepted.Values, Is.All.EqualTo(1));
            Assert.That(accepted.Keys, Is.EquivalentTo(Enumerable.Range(0, producers)
                .SelectMany(producer => Enumerable.Range(0, perProducer).Select(index => $"{producer}:{index}"))));
            Assert.That(Pending(listener).Count, Is.Zero);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task StopAndRestartUnderConcurrentLoadLeavesNoRequestStranded(bool http2, bool dispatchOnThreadPool)
        {
            var prefix = Resources.GetServerAddress();
            using var listener = new Net.HttpListener();
            listener.AddPrefix(prefix);
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var served = 0;
            var serving = Serve(listener, dispatchOnThreadPool, () => Interlocked.Increment(ref served), timeout.Token);
            using var client = Client(http2);
            var load = Load(client, prefix, 16, timeout.Token);
            try
            {
                while (Volatile.Read(ref served) < 200) await Task.Delay(5, timeout.Token);
                // Requests already sent must finish; none are started against the stopped port.
                Volatile.Write(ref load.Stop, 1);
                listener.Stop();
                await serving.WaitAsync(timeout.Token);
                Assert.That(Pending(listener).Count, Is.Zero);
                var outcome = await StopLoad(load);
                Assert.That(outcome.Unexpected, Is.Empty);
                Assert.That(outcome.Succeeded, Is.GreaterThanOrEqualTo(200));

                // A restarted listener admits and serves requests again.
                listener.Start();
                serving = Serve(listener, dispatchOnThreadPool, () => Interlocked.Increment(ref served), timeout.Token);
                using var fresh = Client(http2);
                var again = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => fresh.GetStringAsync(prefix, timeout.Token))).WaitAsync(timeout.Token);
                Assert.That(again, Is.All.EqualTo("ok"));
            }
            finally
            {
                listener.Stop();
                await serving.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DrainUnderConcurrentLoadCompletesEveryAdmittedRequest(bool http2)
        {
            var prefix = Resources.GetServerAddress();
            var handled = 0;
            var handledIds = new ConcurrentQueue<string>();
            var completedIds = new ConcurrentQueue<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix)
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    var requestId = context.Request.Headers["X-EmbedIO-Drain-Test"] ?? "<missing>";
                    handledIds.Enqueue(requestId);
                    _ = Interlocked.Increment(ref handled);
                    await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
                    completedIds.Enqueue(requestId);
                }));
            var running = server.RunAsync(timeout.Token);
            var connector = new DrainConnector();
            using var client = connector.Client(http2);
            var load = Load(client, prefix, 16, timeout.Token);
            while (Volatile.Read(ref handled) < 200) await Task.Delay(5, timeout.Token);

            // Requests already sent must finish; none are started against the drained port.
            Volatile.Write(ref load.Stop, 1);
            connector.BeginDrain();
            await server.DrainAsync(TimeSpan.FromSeconds(10)).WaitAsync(timeout.Token);
            await running.WaitAsync(timeout.Token);
            var outcome = await StopLoad(load);

            Assert.That(outcome.Unexpected, Is.Empty);
            Assert.That(Pending((Net.HttpListener)server.Listener).Count, Is.Zero);
            // Graceful drain answers every request it admitted; refused ones never reach a handler.
            Assert.That(outcome.Succeeded, Is.EqualTo(Volatile.Read(ref handled)),
                "Handler entries: " + string.Join(",", handledIds.TakeLast(32))
                + "; handler writes completed: " + completedIds.Count
                + "; handled without a client response: " + string.Join(",", handledIds.Except(outcome.Delivered))
                + "; duplicate handler entries: " + string.Join(",", handledIds.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key))
                + "; terminal failures: " + string.Join(" | ", outcome.Refused));
            await AssertPortRefusesAsync(prefix);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RequestsRefusedBeforeAdmissionDuringDrainReachATerminalOutcome(bool http2)
        {
            var prefix = Resources.GetServerAddress();
            var lateHandled = 0;
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix)
                .WithModule(new ActionModule("/held", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                    await context.SendStringAsync("held", "text/plain", WebServer.Utf8NoBomEncoding);
                }))
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    _ = Interlocked.Increment(ref lateHandled);
                    await context.SendStringAsync("late", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            var running = server.RunAsync(timeout.Token);
            var connector = new DrainConnector();
            using var client = connector.Client(http2);
            var held = client.GetStringAsync(prefix + "held", timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);

            // The admitted request keeps the drain open while later requests are refused:
            // HTTP/2 streams past the GOAWAY cutoff are refused and retried on a new connection.
            connector.BeginDrain();
            var drain = server.DrainAsync(TimeSpan.FromSeconds(20));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var late = Enumerable.Range(0, 4).Select(async index =>
            {
                try
                {
                    var body = await client.GetStringAsync(prefix + "late/" + index, timeout.Token);
                    return (index, clock.ElapsedMilliseconds, (Exception?)new InvalidOperationException($"Answered '{body}'."));
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return (index, clock.ElapsedMilliseconds, (Exception?)error); }
            }).ToArray();
            var outcomes = await Task.WhenAll(late).WaitAsync(timeout.Token);
            foreach (var (index, elapsed, error) in outcomes)
                TestContext.Out.WriteLine($"late {index}: {elapsed} ms {error?.GetType().Name}: {error?.Message}");
            release.TrySetResult(true);
            string heldOutcome;
            try { heldOutcome = await held.WaitAsync(timeout.Token); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { heldOutcome = error.GetType().Name; }
            TestContext.Out.WriteLine($"held: {heldOutcome} at {clock.ElapsedMilliseconds} ms");
            await drain.WaitAsync(timeout.Token);
            await running.WaitAsync(timeout.Token);

            Assert.That(lateHandled, Is.Zero, "A request sent after drain began was admitted.");
            Assert.That(heldOutcome, Is.EqualTo("held"), "The admitted request must complete during drain.");
            Assert.That(outcomes.Select(outcome => outcome.Item3), Is.All.InstanceOf<HttpRequestException>(),
                "Every request refused before admission must observe a refusal, not the client timeout.");
            Assert.That(outcomes.Select(outcome => DrainConnector.RefusedAtConnect(outcome.Item3)), Is.All.True,
                "Each late request was refused when the client opened a connection, never by a handler.");
            await AssertPortRefusesAsync(prefix);
        }

        // Connections a client opens after drain begins can only reach the closed listening
        // socket. Windows reports each refused loopback connect after about two seconds per
        // address, and SocketsHttpHandler opens one HTTP/2 connection at a time, failing one
        // queued request per attempt. Refuse those attempts here so they cannot outlast the
        // client timeout; AssertPortRefusesAsync checks the real socket once instead.
        private sealed class DrainConnector
        {
            private const string RefusalMessage = "Connection opened after drain began.";
            private int _draining;

            internal void BeginDrain() => Volatile.Write(ref _draining, 1);

            internal HttpClient Client(bool http2) => new(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = ConnectAsync
            })
            {
                DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(10)
            };

            internal static bool RefusedAtConnect(Exception? error)
            {
                for (; error != null; error = error.InnerException)
                    if (error is SocketException && error.Message == RefusalMessage) return true;
                return false;
            }

            private async ValueTask<System.IO.Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
            {
                if (Volatile.Read(ref _draining) != 0)
                    throw new SocketException((int)SocketError.ConnectionRefused, RefusalMessage);
                // Same socket shape as the handler's default connection.
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }

        private static async Task AssertPortRefusesAsync(string prefix)
        {
            using var probe = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var error = await Assert.CatchAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, new Uri(prefix).Port));
            Assert.That(error?.SocketErrorCode, Is.EqualTo(SocketError.ConnectionRefused), "The drained port still accepts connections.");
        }

        private static HttpClient Client(bool http2) => new(new SocketsHttpHandler { UseProxy = false })
        {
            DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10)
        };

        [TestCase(false)]
        [TestCase(true)]
        public async Task ResponseFixtureAcceptsAnAlreadyCanceledHttp2Stream(bool dispatchOnThreadPool)
        {
            var prefix = Resources.GetServerAddress();
            using var listener = new Net.HttpListener();
            listener.AddPrefix(prefix); listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = Client(true);
            var request = client.GetStringAsync(prefix, timeout.Token);
            try
            {
                var context = await listener.GetContextAsync(timeout.Token);
                var exchange = context.GetType().GetField("_exchange", PrivateInstance)?.GetValue(context)
                    ?? throw new AssertionException("Missing HTTP2 exchange.");
                var cancel = exchange.GetType().GetMethod("Cancel", PrivateInstance)
                    ?? throw new AssertionException("Missing stream cancellation.");
                cancel.Invoke(exchange, new object[] { new OperationCanceledException("Controlled stream shutdown.") });
                Assert.That(context.CancellationToken.IsCancellationRequested, Is.True);
                var served = 0;
                if (dispatchOnThreadPool) await Task.Run(() => Respond(context, () => Interlocked.Increment(ref served)), timeout.Token);
                else await Respond(context, () => Interlocked.Increment(ref served));
                Assert.That(served, Is.Zero, "An aborted response is not counted as successfully served.");
            }
            finally
            {
                listener.Stop();
                try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (HttpRequestException) { }
            }
        }
        private static async Task Serve(Net.HttpListener listener, bool dispatchOnThreadPool, Action onServed, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    var context = await listener.GetContextAsync(token);
                    if (dispatchOnThreadPool) _ = Task.Run(() => Respond(context, onServed), token);
                    else await Respond(context, onServed);
                }
            }
            catch (HttpListenerException error) when (error.ErrorCode == 995) { }
        }

        private static async Task Respond(IHttpContextImpl context, Action onServed)
        {
            try
            {
                var body = Encoding.ASCII.GetBytes("ok");
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Close();
                onServed();
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                // HTTP/2 Stop/reset cancels the request token before disposing its response.
                // This is a terminal response outcome, not a successfully served request.
            }
            catch (Exception error) when (error is System.IO.IOException or ObjectDisposedException or HttpListenerException)
            {
                // Stop can close the connection of a context accepted just before it.
            }
        }

        private sealed class LoadState
        {
            internal int Stop;
            internal int Succeeded;
            internal int NextRequestId;
            internal readonly ConcurrentQueue<string> Delivered = new();
            internal readonly ConcurrentQueue<string> Refused = new();
            internal readonly ConcurrentQueue<Exception> Unexpected = new();
            internal Task[] Workers = Array.Empty<Task>();
        }

        private static LoadState Load(HttpClient client, string prefix, int workers, CancellationToken token)
        {
            var state = new LoadState();
            state.Workers = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                while (Volatile.Read(ref state.Stop) == 0)
                {
                    var requestId = Interlocked.Increment(ref state.NextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, prefix)
                        { Version = client.DefaultRequestVersion, VersionPolicy = client.DefaultVersionPolicy };
                        request.Headers.Add("X-EmbedIO-Drain-Test", requestId);
                        using var response = await client.SendAsync(request, token);
                        response.EnsureSuccessStatusCode();
                        var body = await response.Content.ReadAsStringAsync(token);
                        if (body != "ok") state.Unexpected.Enqueue(new InvalidOperationException($"Unexpected body '{body}'."));
                        else { state.Delivered.Enqueue(requestId); _ = Interlocked.Increment(ref state.Succeeded); }
                    }
                    catch (HttpRequestException error)
                    {
                        state.Refused.Enqueue(requestId + ": " + error);
                        // Refused, reset or closed by Stop/drain: the request was not answered,
                        // but the client saw a terminal outcome rather than a hang.
                        await Task.Delay(1, token);
                    }
                    catch (TaskCanceledException error) when (!token.IsCancellationRequested)
                    {
                        state.Unexpected.Enqueue(new TimeoutException("A request was left unanswered.", error));
                        return;
                    }
                }
            }, token)).ToArray();
            return state;
        }

        private static async Task<(int Succeeded, Exception[] Unexpected, string[] Delivered, string[] Refused)> StopLoad(LoadState state)
        {
            Volatile.Write(ref state.Stop, 1);
            await Task.WhenAll(state.Workers).WaitAsync(TimeSpan.FromSeconds(20));
            return (Volatile.Read(ref state.Succeeded), state.Unexpected.ToArray(), state.Delivered.ToArray(), state.Refused.ToArray());
        }

        private static IDictionary Pending(Net.HttpListener listener)
            => (IDictionary)(typeof(Net.HttpListener).GetField("_ctxQueue", PrivateInstance)?.GetValue(listener)
                ?? throw new AssertionException("Missing pending context map."));

        private static int Order(Net.HttpListener listener)
            => ((ICollection)(typeof(Net.HttpListener).GetField("_ctxOrder", PrivateInstance)?.GetValue(listener)
                ?? throw new AssertionException("Missing pending context order."))).Count;

        // A context with only an identifier; anything else returns its default.
        public class FakeContext : DispatchProxy
        {
            private string _id = string.Empty;

            internal static IHttpContextImpl Create(string id)
            {
                var context = Create<IHttpContextImpl, FakeContext>();
                ((FakeContext)(object)context)._id = id;
                return context;
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod?.Name == "get_Id") return _id;
                var type = targetMethod?.ReturnType;
                return type != null && type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
        }
    }
}
