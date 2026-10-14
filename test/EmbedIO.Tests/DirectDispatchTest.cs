using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The server hands each admitted request straight to its handler on the
    // thread that admitted it: the TCP connection's own thread, or the HTTP/3
    // request stream's task. These cases pin the lifecycle semantics that must
    // not change with that path: ordering, failure, cancellation, drain, HTTP/2
    // and HTTP/3 concurrency, blocking handlers and the pull API that standalone
    // listeners keep.
    public class DirectDispatchTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object Field(object owner, string name)
            => owner.GetType().GetField(name, Hidden)?.GetValue(owner) ?? throw new AssertionException("Missing field " + name);

        private static bool QuicUnavailable()
            => !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                || !QuicListener.IsSupported || !QuicConnection.IsSupported
                || typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") == null;

        private static HttpClient Http3Client(X509Certificate2 certificate)
        {
            var client = HttpsSmoke.CreateClient(certificate, new SocketsHttpHandler { UseProxy = false });
            client.DefaultRequestVersion = HttpVersion.Version30;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return client;
        }

        // True when the caller runs inside the HTTP/3 session's dispatch, that is on
        // the request stream's own task and not on a work item of the pull loop.
        private static bool DispatchedFromHttp3Session()
            => new System.Diagnostics.StackTrace().GetFrames()
                .Any(frame => frame.GetMethod()?.DeclaringType?.FullName?.Contains("Http3Listener", StringComparison.Ordinal) == true);

        private static int QueuedContexts(object session)
        {
            var contexts = Field(session, "Contexts");
            var reader = contexts.GetType().GetProperty("Reader")?.GetValue(contexts) ?? throw new AssertionException("Missing channel reader.");
            return (int)(reader.GetType().GetProperty("Count")?.GetValue(reader) ?? throw new AssertionException("Missing channel count."));
        }

        private static string PlainUrl()
            => new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = "http" }.Uri.ToString();

        private static HttpClient SingleConnectionClient(Version? version = null)
        {
            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 })
            { Timeout = TimeSpan.FromSeconds(20) };
            if (version != null)
            {
                client.DefaultRequestVersion = version;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }
            return client;
        }

        private static async Task<Task> StartAsync(WebServer server, CancellationToken token)
        {
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) => { if (change.NewState == WebServerState.Listening) listening.TrySetResult(); };
            var running = server.RunAsync(token);
            var first = await Task.WhenAny(listening.Task, running).WaitAsync(token);
            if (first == running) { await running; Assert.Fail("The server stopped before it was listening."); }
            return running;
        }

        [Test]
        public async Task KeepAliveRequestsAreDispatchedWithoutTheAcceptQueue()
        {
            var url = PlainUrl();
            var served = 0;
            var ports = new HashSet<int>();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(context =>
                {
                    lock (ports) ports.Add(context.RemoteEndPoint.Port);
                    return context.SendStringAsync(Interlocked.Increment(ref served).ToString(), "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            var coordinator = Field(server.Listener, "_coordinator");
            Assert.That(Field(coordinator, "_dispatcher"), Is.Not.Null, "The server must install its dispatcher before serving.");
            using var client = SingleConnectionClient();
            for (var request = 1; request <= 64; request++)
                Assert.That(await client.GetStringAsync(url + "count", stop.Token), Is.EqualTo(request.ToString()));
            Assert.That(ports, Has.Count.EqualTo(1), "All requests must reuse one keep-alive connection.");
            Assert.That(((IDictionary)Field(Field(coordinator, "_epoch"), "_pending")).Count, Is.Zero, "Directly dispatched requests never enter the accept queue.");
            stop.Cancel();
            await running;
        }

        [Test]
        public async Task PipelinedRequestsOnOneConnectionAreAnsweredInOrder()
        {
            var url = new Uri(PlainUrl());
            var served = 0;
            using var server = new WebServer(options => options.WithUrlPrefix(url.ToString()).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    // Completes synchronously for tiny bodies, so the actor's inline
                    // cycle budget, not the handler, must yield the thread.
                    var body = Encoding.ASCII.GetBytes(Interlocked.Increment(ref served).ToString());
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, 0, body.Length, context.CancellationToken);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            const int count = 200;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(IPAddress.Loopback, url.Port, stop.Token);
            using var stream = new NetworkStream(socket, true);
            var requests = new StringBuilder();
            for (var request = 0; request < count; request++)
                requests.Append("GET /n").Append(request).Append(" HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(requests.ToString());
            await stream.WriteAsync(bytes, stop.Token);
            var bodies = await ReadResponsesAsync(stream, count, stop.Token);
            for (var index = 0; index < count; index++)
                Assert.That(bodies[index], Is.EqualTo((index + 1).ToString()), $"Response {index} arrived out of order.");
            stop.Cancel();
            await running;
        }

        [Test]
        public async Task ThrowingHandlerAnswers500AndTheServerKeepsServing()
        {
            var url = PlainUrl();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(context => context.RequestedPath == "/boom"
                    ? throw new InvalidOperationException("Handler failure under direct dispatch.")
                    : context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            using var client = SingleConnectionClient();
            using (var failed = await client.GetAsync(url + "boom", stop.Token))
            {
                Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(failed.Headers.ConnectionClose, Is.True);
            }
            Assert.That(await client.GetStringAsync(url + "ok", stop.Token), Is.EqualTo("ok"));
            stop.Cancel();
            await running;
        }

        [Test]
        public async Task HandlerOutlivingItsResponseDoesNotHoldTheNextKeepAliveRequest()
        {
            var url = PlainUrl();
            var lingering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    await context.SendStringAsync(context.RequestedPath, "text/plain", WebServer.Utf8NoBomEncoding);
                    if (context.RequestedPath != "/linger") return;
                    // The response is complete; the handler keeps running, as the
                    // contract allows, and must not block this connection's next request.
                    lingering.TrySetResult();
                    await release.Task;
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            try
            {
                using var client = SingleConnectionClient();
                Assert.That(await client.GetStringAsync(url + "linger", stop.Token), Is.EqualTo("/linger"));
                await lingering.Task.WaitAsync(stop.Token);
                Assert.That(await client.GetStringAsync(url + "next", stop.Token), Is.EqualTo("/next"), "The next keep-alive request is served while the previous handler still runs.");
                var drain = server.DrainAsync(TimeSpan.FromSeconds(10), stop.Token);
                await drain.WaitAsync(stop.Token);
                Assert.That(release.Task.IsCompleted, Is.False, "The drain completed without waiting for a handler whose response has ended.");
            }
            finally { release.TrySetResult(); }
            await running.WaitAsync(stop.Token);
        }

        [Test]
        public async Task HandlerFailingAfterItsResponseIsLoggedAndTheConnectionStaysHealthy()
        {
            var url = PlainUrl();
            var marker = "late-failure-" + Guid.NewGuid().ToString("N");
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    await context.SendStringAsync(context.RequestedPath, "text/plain", WebServer.Utf8NoBomEncoding);
                    if (context.RequestedPath != "/late") return;
                    await Task.Yield();
                    throw new InvalidOperationException(marker);
                }));
            var records = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var trace = new RecordingTrace(records);
            var source = Diagnostics.Log.Source;
            var previousLevel = source.Switch.Level;
            source.Listeners.Add(trace);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                source.Switch.Level = System.Diagnostics.SourceLevels.All;
                var running = await StartAsync(server, stop.Token);
                using var client = SingleConnectionClient();
                Assert.That(await client.GetStringAsync(url + "late", stop.Token), Is.EqualTo("/late"));
                Assert.That(await client.GetStringAsync(url + "after", stop.Token), Is.EqualTo("/after"), "The connection serves the next request after a post-response failure.");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!records.Any(record => record.Contains(marker, StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
                    await Task.Delay(20, stop.Token);
                Assert.That(records.Any(record => record.Contains(marker, StringComparison.Ordinal)), Is.True, "The failure that followed the completed response is logged.");
                stop.Cancel();
                await running;
            }
            finally
            {
                source.Listeners.Remove(trace);
                source.Switch.Level = previousLevel;
            }
        }

        private sealed class RecordingTrace(System.Collections.Concurrent.ConcurrentQueue<string> records) : System.Diagnostics.TraceListener
        {
            public override void Write(string? message) => records.Enqueue(message ?? string.Empty);
            public override void WriteLine(string? message) => records.Enqueue(message ?? string.Empty);
        }

        [Test]
        public async Task HandlerWaitingForCancellationEndsWithTheServer()
        {
            var url = PlainUrl();
            var observedCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                    finally { observedCancellation.TrySetResult(context.CancellationToken.IsCancellationRequested); }
                }));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stop = new CancellationTokenSource();
            var running = await StartAsync(server, stop.Token);
            using var client = SingleConnectionClient();
            var pending = client.GetAsync(url + "wait", deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            stop.Cancel();
            await running.WaitAsync(deadline.Token);
            Assert.That(await observedCancellation.Task.WaitAsync(deadline.Token), Is.True, "The handler must observe the server token.");
            await Assert.ThrowsAsync<HttpRequestException>(async () => { using var response = await pending; _ = await response.Content.ReadAsStringAsync(deadline.Token); });
        }

        [Test]
        public async Task DrainWaitsForTheInFlightHandlerAndRefusesNewConnections()
        {
            var url = PlainUrl();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync("slow", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stop = new CancellationTokenSource();
            var running = await StartAsync(server, stop.Token);
            using var first = SingleConnectionClient();
            var slow = first.GetStringAsync(url + "slow", deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            var drain = server.DrainAsync(TimeSpan.FromSeconds(20), deadline.Token);
            await Task.Delay(200, deadline.Token);
            Assert.That(drain.IsCompleted, Is.False, "The drain must wait for the handler that is still running.");
            Assert.That(slow.IsCompleted, Is.False);
            using (var second = SingleConnectionClient())
                await Assert.ThrowsAsync<HttpRequestException>(() => second.GetStringAsync(url + "late", deadline.Token),
                    "A draining listener must not accept new connections.");
            release.TrySetResult();
            Assert.That(await slow, Is.EqualTo("slow"));
            await drain.WaitAsync(deadline.Token);
            await running.WaitAsync(deadline.Token);
        }

        [Test]
        public async Task Http2StreamsOnOneConnectionAreNotSerialized()
        {
            var url = PlainUrl();
            var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    if (context.RequestedPath == "/slow")
                    {
                        slowEntered.TrySetResult();
                        await release.Task.WaitAsync(context.CancellationToken);
                    }
                    await context.SendStringAsync(context.RequestedPath, "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            using var client = SingleConnectionClient(HttpVersion.Version20);
            var slow = client.GetAsync(url + "slow", stop.Token);
            await slowEntered.Task.WaitAsync(stop.Token);
            using (var fast = await client.GetAsync(url + "fast", stop.Token))
            {
                Assert.That(fast.Version, Is.EqualTo(HttpVersion.Version20));
                Assert.That(await fast.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("/fast"));
            }
            Assert.That(slow.IsCompleted, Is.False, "A later stream completed, so the earlier handler did not block it.");
            release.TrySetResult();
            using var slowResponse = await slow;
            Assert.That(slowResponse.Version, Is.EqualTo(HttpVersion.Version20));
            Assert.That(await slowResponse.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("/slow"));
            stop.Cancel();
            await running;
        }

        [Test]
        public async Task BlockingHandlersDoNotStallOtherConnections()
        {
            var url = PlainUrl();
            using var gate = new ManualResetEventSlim(false);
            var blocked = 0;
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(context =>
                {
                    if (context.RequestedPath == "/block")
                    {
                        // A synchronous block on the dispatching thread, the worst case
                        // for a handler that now runs on the connection's continuation.
                        Interlocked.Increment(ref blocked);
                        gate.Wait(context.CancellationToken);
                    }
                    return context.SendStringAsync(context.RequestedPath, "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            var blockers = new List<HttpClient>();
            var blocking = new List<Task<string>>();
            try
            {
                for (var connection = 0; connection < 2; connection++)
                {
                    var client = SingleConnectionClient();
                    blockers.Add(client);
                    blocking.Add(client.GetStringAsync(url + "block", stop.Token));
                }
                while (Volatile.Read(ref blocked) < blockers.Count) await Task.Delay(10, stop.Token);
                using var fast = SingleConnectionClient();
                for (var request = 0; request < 20; request++)
                    Assert.That(await fast.GetStringAsync(url + "fast", stop.Token), Is.EqualTo("/fast"));
                Assert.That(blocking, Has.All.Property(nameof(Task.IsCompleted)).False);
                gate.Set();
                foreach (var response in blocking) Assert.That(await response, Is.EqualTo("/block"));
            }
            finally
            {
                gate.Set();
                foreach (var client in blockers) client.Dispose();
            }
            stop.Cancel();
            await running;
        }

        [Test]
        public async Task RegistrationQueuesForGetContextAsyncOnlyWithoutADispatcher()
        {
            var contextType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpListenerContext", true)
                ?? throw new AssertionException("Missing context type.");
            var id = contextType.GetField("<Id>k__BackingField", Hidden) ?? throw new AssertionException("Missing context id.");
            var register = (typeof(Net.HttpListener).GetMethod("RegisterContext", Hidden) ?? throw new AssertionException("Missing registration."))
                .CreateDelegate<Func<Net.HttpListener, IHttpContextImpl, Task?>>();
            var dispatching = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.IDispatchingHttpListener", true)
                ?? throw new AssertionException("Missing dispatch interface.");
            var install = dispatching.GetMethod("SetDispatcher") ?? throw new AssertionException("Missing dispatcher installation.");
            IHttpContextImpl Context(string name)
            {
                var context = RuntimeHelpers.GetUninitializedObject(contextType);
                id.SetValue(context, name);
                return (IHttpContextImpl)context;
            }

            using var listener = new Net.HttpListener();
            listener.Start(); // No prefixes: the queue is exercised without sockets.
            var pending = (IDictionary)Field(Field(Field(listener, "_coordinator"), "_epoch"), "_pending");
            var queued = Context("queued");
            Assert.That(register(listener, queued), Is.Null, "Without a dispatcher a registration is queued.");
            Assert.That(pending.Count, Is.EqualTo(1));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.That(await listener.GetContextAsync(deadline.Token), Is.SameAs(queued));
            Assert.That(pending.Count, Is.Zero);

            IHttpContextImpl? received = null;
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            install.Invoke(listener, new object[] { new Func<IHttpContextImpl, Task>(context => { received = context; return handled.Task; }) });
            var direct = Context("direct");
            Assert.That(register(listener, direct), Is.SameAs(handled.Task), "With a dispatcher the handler's task is returned.");
            Assert.That(received, Is.SameAs(direct));
            Assert.That(pending.Count, Is.Zero, "A dispatched request never enters the queue.");
            using var canceled = new CancellationTokenSource(100);
            await Assert.ThrowsAsync<OperationCanceledException>(() => listener.GetContextAsync(canceled.Token), "Nothing is queued for the pull API.");

            listener.Stop();
            Assert.That(() => register(listener, Context("late")), Throws.TypeOf<HttpListenerException>(), "A stopped listener refuses registration on either path.");
            handled.TrySetResult();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PropagatingHandlerFaultIsObservedAndLogged(bool afterSuspension)
        {
            var url = PlainUrl();
            var marker = "propagating-" + Guid.NewGuid().ToString("N");
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    if (context.RequestedPath != "/fatal") { await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding); return; }
                    if (afterSuspension) await Task.Yield();
                    // Not recoverable by the server's policy, so it leaves the handler
                    // task faulted; the server must still observe and log it.
                    throw new OutOfMemoryException(marker);
                }));
            var records = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var trace = new RecordingTrace(records);
            var source = Diagnostics.Log.Source;
            var previousLevel = source.Switch.Level;
            source.Listeners.Add(trace);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                source.Switch.Level = System.Diagnostics.SourceLevels.All;
                var running = await StartAsync(server, stop.Token);
                using var client = SingleConnectionClient();
                using (var faulted = await client.GetAsync(url + "fatal", stop.Token))
                    Assert.That((int)faulted.StatusCode, Is.GreaterThanOrEqualTo(200), "The context still closes with a final response.");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!records.Any(record => record.Contains(marker, StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
                    await Task.Delay(20, stop.Token);
                Assert.That(records.Any(record => record.Contains(marker, StringComparison.Ordinal) && record.Contains("faulted after dispatch", StringComparison.Ordinal)), Is.True,
                    "The server observes and logs a fault the handler propagated.");
                using var healthy = SingleConnectionClient();
                Assert.That(await healthy.GetStringAsync(url + "ok", stop.Token), Is.EqualTo("ok"));
                stop.Cancel();
                await running;
            }
            finally
            {
                source.Listeners.Remove(trace);
                source.Switch.Level = previousLevel;
            }
        }

        [Test]
        public async Task DispatchFailureBeforeHandlingAnswers500AndClosesTheContext()
        {
            using var server = new WebServer(options => options.WithUrlPrefix(PlainUrl()).WithMode(HttpListenerMode.EmbedIO));
            var dispatch = typeof(WebServer).GetMethod("DispatchAsync", Hidden) ?? throw new AssertionException("Missing dispatch method.");
            var response = RecordingResponse.Create();
            var context = FailingContext.Create(response);
            var records = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var trace = new RecordingTrace(records);
            var source = Diagnostics.Log.Source;
            var previousLevel = source.Switch.Level;
            source.Listeners.Add(trace);
            try
            {
                source.Switch.Level = System.Diagnostics.SourceLevels.All;
                var handling = (Task)(dispatch.Invoke(server, new object[] { context, CancellationToken.None }) ?? throw new AssertionException("Dispatch returned no task."));
                await handling.WaitAsync(TimeSpan.FromSeconds(10));
                var recorded = (RecordingResponse)(object)response;
                Assert.That(recorded.StatusCode, Is.EqualTo(500), "A failure before the handler starts answers like an unhandled failure.");
                Assert.That(recorded.KeepAlive, Is.False, "The connection closes after that answer.");
                Assert.That(((FailingContext)(object)context).Closed, Is.EqualTo(1), "The context is closed exactly once.");
                Assert.That(records.Any(record => record.Contains("Request dispatch failed before handling", StringComparison.Ordinal)), Is.True, "The failure is logged.");
            }
            finally
            {
                source.Listeners.Remove(trace);
                source.Switch.Level = previousLevel;
            }
        }

        // The connection discards the dispatch task and waits for the response to
        // end, so a context whose Close fails must still end its response: the
        // failure is logged and the response aborted, and the task never faults.
        [Test]
        public async Task DispatchFailureWhoseCloseThrowsIsLoggedAndTheTaskCompletes()
        {
            using var server = new WebServer(options => options.WithUrlPrefix(PlainUrl()).WithMode(HttpListenerMode.EmbedIO));
            var dispatch = typeof(WebServer).GetMethod("DispatchAsync", Hidden) ?? throw new AssertionException("Missing dispatch method.");
            var response = RecordingResponse.Create();
            var context = FailingContext.Create(response, new IOException("The transport is gone."));
            var records = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var trace = new RecordingTrace(records);
            var source = Diagnostics.Log.Source;
            var previousLevel = source.Switch.Level;
            source.Listeners.Add(trace);
            try
            {
                source.Switch.Level = System.Diagnostics.SourceLevels.All;
                var handling = (Task)(dispatch.Invoke(server, new object[] { context, CancellationToken.None }) ?? throw new AssertionException("Dispatch returned no task."));
                await handling.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(handling.Status, Is.EqualTo(TaskStatus.RanToCompletion), "The dispatch task does not fault: nothing awaits it.");
                Assert.That(((RecordingResponse)(object)response).StatusCode, Is.EqualTo(500), "The answer was prepared before the close failed.");
                Assert.That(((FailingContext)(object)context).Closed, Is.EqualTo(1), "Close was attempted exactly once.");
                Assert.That(records.Any(record => record.Contains("Request dispatch failed before handling", StringComparison.Ordinal)), Is.True, "The dispatch failure is logged.");
                Assert.That(records.Any(record => record.Contains("Closing the failed request's context failed", StringComparison.Ordinal)), Is.True, "The close failure is logged before the abort.");
            }
            finally
            {
                source.Listeners.Remove(trace);
                source.Switch.Level = previousLevel;
            }
        }

        public class RecordingResponse : DispatchProxy
        {
            internal int StatusCode { get; private set; }
            internal bool? KeepAlive { get; private set; }
            internal static IHttpResponse Create() => Create<IHttpResponse, RecordingResponse>();
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                switch (targetMethod?.Name)
                {
                    case "set_StatusCode": StatusCode = (int)(args?[0] ?? 0); return null;
                    case "set_KeepAlive": KeepAlive = (bool)(args?[0] ?? false); return null;
                }
                var type = targetMethod?.ReturnType;
                return type != null && type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
        }

        // The request is unavailable, as it would be if context preparation failed before any handler ran.
        public class FailingContext : DispatchProxy
        {
            private IHttpResponse? _response;
            private Exception? _closeFailure;
            internal int Closed { get; private set; }
            internal static IHttpContextImpl Create(IHttpResponse response, Exception? closeFailure = null)
            {
                var context = Create<IHttpContextImpl, FailingContext>();
                var failing = (FailingContext)(object)context;
                failing._response = response;
                failing._closeFailure = closeFailure;
                return context;
            }
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                switch (targetMethod?.Name)
                {
                    case "get_Id": return "failing-dispatch";
                    case "get_Request": throw new InvalidOperationException("The request is unavailable before handling.");
                    case "get_Response": return _response;
                    case "Close":
                        Closed++;
                        if (_closeFailure != null) throw _closeFailure;
                        return null;
                }
                var type = targetMethod?.ReturnType;
                return type != null && type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
        }

        [Test]
        public async Task CombinedModeDispatchesTcpRequestsDirectly()
        {
            if (QuicUnavailable())
            {
                Assert.Ignore("Combined mode requires a QUIC provider on this host.");
                return;
            }
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            var url = CombinedPrefix();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIOCombined).WithCertificate(certificate))
                .WithModule(new ActionModule(context => context.SendStringAsync("combined", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            var tcp = Field(Field(server.Listener, "_session"), "_tcp");
            Assert.That(Field(Field(tcp, "_coordinator"), "_dispatcher"), Is.Not.Null, "The combined listener forwards the dispatcher to its TCP transport.");
            using var client = HttpsSmoke.CreateClient(certificate, new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 });
            using var response = await client.GetAsync(url + "direct", stop.Token);
            Assert.That(response.Version, Is.EqualTo(HttpVersion.Version11));
            Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("combined"));
            Assert.That(((IDictionary)Field(Field(Field(tcp, "_coordinator"), "_epoch"), "_pending")).Count, Is.Zero);
            Assert.That(Field(Field(Field(server.Listener, "_session"), "_quic"), "_dispatcher"), Is.Not.Null, "The combined listener forwards the dispatcher to its QUIC transport.");
            stop.Cancel();
            await running;
        }

        [Test]
        public async Task Http3RequestsStartTheHandlerOnTheRequestStream()
        {
            if (QuicUnavailable())
            {
                Assert.Ignore("HTTP/3 requires a QUIC provider on this host.");
                return;
            }
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            var url = CombinedPrefix();
            var inline = 0;
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule(context =>
                {
                    if (DispatchedFromHttp3Session()) Interlocked.Increment(ref inline);
                    return context.SendStringAsync("quic", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            var session = Field(server.Listener, "_session");
            Assert.That(Field(session, "_dispatcher"), Is.Not.Null, "The HTTP/3 session received the server's dispatcher.");
            using var client = Http3Client(certificate);
            for (var request = 0; request < 3; request++)
            {
                using var response = await client.GetAsync(url + "direct", stop.Token);
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30));
                Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("quic"));
            }
            Assert.That(inline, Is.EqualTo(3), "Every handler started inside the session's dispatch, on the request stream's own task.");
            Assert.That(QueuedContexts(session), Is.Zero, "No request passed through the session's context channel.");
            stop.Cancel();
            await running;
        }

        [Test]
        public async Task Http3ThrowingHandlerAnswers500AndTheConnectionKeepsServing()
        {
            if (QuicUnavailable())
            {
                Assert.Ignore("HTTP/3 requires a QUIC provider on this host.");
                return;
            }
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            var url = CombinedPrefix();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule(context => context.RequestedPath == "/boom"
                    ? throw new InvalidOperationException("Handler failure under direct dispatch.")
                    : context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            using var client = Http3Client(certificate);
            using (var failed = await client.GetAsync(url + "boom", stop.Token))
            {
                Assert.That(failed.Version, Is.EqualTo(HttpVersion.Version30));
                Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            }
            using var answered = await client.GetAsync(url + "ok", stop.Token);
            Assert.That(answered.Version, Is.EqualTo(HttpVersion.Version30));
            Assert.That(await answered.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("ok"));
            stop.Cancel();
            await running;
        }

        [Test]
        public async Task Http3BlockingHandlerDoesNotStallAnotherStream()
        {
            if (QuicUnavailable())
            {
                Assert.Ignore("HTTP/3 requires a QUIC provider on this host.");
                return;
            }
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            var url = CombinedPrefix();
            using var gate = new ManualResetEventSlim(false);
            var blocked = 0;
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule(context =>
                {
                    if (context.RequestedPath == "/block")
                    {
                        // A synchronous block before the first suspension holds the
                        // request stream's task; the connection's other streams go on.
                        Interlocked.Increment(ref blocked);
                        gate.Wait(context.CancellationToken);
                    }
                    return context.SendStringAsync(context.RequestedPath, "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = await StartAsync(server, stop.Token);
            using var client = Http3Client(certificate);
            try
            {
                var blocking = client.GetStringAsync(url + "block", stop.Token);
                while (Volatile.Read(ref blocked) == 0) await Task.Delay(10, stop.Token);
                for (var request = 0; request < 10; request++)
                    Assert.That(await client.GetStringAsync(url + "fast", stop.Token), Is.EqualTo("/fast"));
                Assert.That(blocking.IsCompleted, Is.False);
                gate.Set();
                Assert.That(await blocking, Is.EqualTo("/block"));
            }
            finally
            {
                gate.Set();
            }
            stop.Cancel();
            await running;
        }

        private static string CombinedPrefix()
        {
            for (var attempt = 0; ; attempt++)
            {
                using var tcp4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var udp4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                using var tcp6 = Socket.OSSupportsIPv6 ? new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) : null;
                using var udp6 = Socket.OSSupportsIPv6 ? new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) : null;
                try
                {
                    // Both transports bind the same number on every loopback family the listener uses.
                    var port = Random.Shared.Next(10000, 30000);
                    tcp4.ExclusiveAddressUse = true;
                    tcp4.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    udp4.ExclusiveAddressUse = true;
                    udp4.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    if (tcp6 != null && udp6 != null)
                    {
                        tcp6.DualMode = false;
                        udp6.DualMode = false;
                        tcp6.ExclusiveAddressUse = true;
                        udp6.ExclusiveAddressUse = true;
                        tcp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                        udp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                    }
                    return $"https://localhost:{port}/";
                }
                catch (SocketException error) when (attempt < 31 && error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                {
                    // Retry only the port selection.
                }
            }
        }

        // A drain lets the request in progress finish and then closes the connection
        // before the pipelined successor is parsed, exactly as on the pull path; the
        // drain admission branch of Register serves only a head that was read before the
        // drain marked its connection, and HTTP/2 streams on already admitted connections.
        [Test]
        public async Task DrainFinishesTheInFlightResponseAndClosesBeforeThePipelinedSuccessor()
        {
            var url = new Uri(PlainUrl());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(options => options.WithUrlPrefix(url.ToString()).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    if (context.RequestedPath == "/first")
                    {
                        entered.TrySetResult();
                        await release.Task;
                    }
                    var body = Encoding.ASCII.GetBytes(context.RequestedPath);
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, 0, body.Length, context.CancellationToken);
                }));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stop = new CancellationTokenSource();
            var running = await StartAsync(server, stop.Token);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(IPAddress.Loopback, url.Port, deadline.Token);
            using var stream = new NetworkStream(socket, true);
            // The second request is pipelined, so the connection is never idle: the drain
            // admits it through the connection's own admission rather than the accept loop.
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /first HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\nGET /second HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"), deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            var drain = server.DrainAsync(TimeSpan.FromSeconds(20), deadline.Token);
            await Task.Delay(200, deadline.Token);
            Assert.That(drain.IsCompleted, Is.False, "The drain waits for the request in progress.");
            release.TrySetResult();
            var bodies = await ReadResponsesAsync(stream, 1, deadline.Token);
            Assert.That(bodies, Is.EqualTo(new[] { "/first" }), "The response in progress completes during the drain.");
            await drain.WaitAsync(deadline.Token);
            var tail = new byte[64];
            Assert.That(await stream.ReadAsync(tail, deadline.Token), Is.Zero, "The drained connection closes instead of serving the pipelined successor.");
            await running.WaitAsync(deadline.Token);
        }

        [Test]
        public async Task PipelinedBurstDoesNotStarveAnotherConnection()
        {
            var url = new Uri(PlainUrl());
            using var server = new WebServer(options => options.WithUrlPrefix(url.ToString()).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule(async context =>
                {
                    // Completes synchronously, so the pipelined connection could run
                    // on one worker indefinitely without the actor's periodic yield.
                    var body = Encoding.ASCII.GetBytes(context.RequestedPath);
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, 0, body.Length, context.CancellationToken);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var running = await StartAsync(server, stop.Token);
            const int burst = 4000;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(IPAddress.Loopback, url.Port, stop.Token);
            using var stream = new NetworkStream(socket, true);
            var requests = new StringBuilder();
            for (var request = 0; request < burst; request++) requests.Append("GET /b HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            var sending = stream.WriteAsync(Encoding.ASCII.GetBytes(requests.ToString()), stop.Token).AsTask();
            var reading = ReadResponsesAsync(stream, burst, stop.Token);
            using var other = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 }) { Timeout = TimeSpan.FromSeconds(5) };
            for (var request = 0; request < 20; request++)
                Assert.That(await other.GetStringAsync(url + "other", stop.Token), Is.EqualTo("/other"), "Another connection is served while the burst is in progress.");
            await sending;
            var bodies = await reading;
            Assert.That(bodies, Has.Count.EqualTo(burst));
            Assert.That(bodies, Has.All.EqualTo("/b"));
            stop.Cancel();
            await running;
        }

        private static async Task<List<string>> ReadResponsesAsync(NetworkStream stream, int count, CancellationToken token)
        {
            var bodies = new List<string>();
            var buffer = new byte[65536];
            var data = new MemoryStream();
            var consumed = 0;
            while (bodies.Count < count)
            {
                var bytes = data.GetBuffer();
                var length = (int)data.Length;
                var headerEnd = IndexOf(bytes, consumed, length, "\r\n\r\n");
                if (headerEnd >= 0)
                {
                    var head = Encoding.ASCII.GetString(bytes, consumed, headerEnd - consumed);
                    Assert.That(head, Does.StartWith("HTTP/1.1 200"));
                    var contentLength = ContentLength(head);
                    var bodyStart = headerEnd + 4;
                    if (length - bodyStart >= contentLength)
                    {
                        bodies.Add(Encoding.ASCII.GetString(bytes, bodyStart, contentLength));
                        consumed = bodyStart + contentLength;
                        continue;
                    }
                }
                var read = await stream.ReadAsync(buffer, token);
                if (read == 0) throw new EndOfStreamException($"The connection closed after {bodies.Count} responses.");
                data.Write(buffer, 0, read);
            }
            return bodies;
        }

        private static int IndexOf(byte[] bytes, int start, int end, string marker)
        {
            for (var index = start; index <= end - marker.Length; index++)
            {
                var match = true;
                for (var offset = 0; offset < marker.Length && match; offset++) match = bytes[index + offset] == marker[offset];
                if (match) return index;
            }
            return -1;
        }

        private static int ContentLength(string head)
        {
            foreach (var line in head.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    return int.Parse(line.Substring("Content-Length:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            throw new AssertionException("Response without Content-Length: " + head);
        }
    }
}
