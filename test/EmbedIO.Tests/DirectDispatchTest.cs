using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The server hands each admitted TCP request straight to its handler on the
    // connection's own thread. These cases pin the lifecycle semantics that must
    // not change with that path: ordering, failure, cancellation, drain, HTTP/2
    // concurrency, blocking handlers and the pull API that standalone listeners keep.
    public class DirectDispatchTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object Field(object owner, string name)
            => owner.GetType().GetField(name, Hidden)?.GetValue(owner) ?? throw new AssertionException("Missing field " + name);

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
