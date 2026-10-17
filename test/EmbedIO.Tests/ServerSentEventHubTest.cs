using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Authorization;
using EmbedIO.OpenApi;
using EmbedIO.PlatformTests;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class ServerSentEventHubTest
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        [TestCase(10)]
        [TestCase(11)]
        [TestCase(12)]
        [TestCase(13)]
        public void InvalidPoliciesAreRejected(int mode)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = mode switch
            {
                0 => new ServerSentEventHubOptions(maxEventBytes: 0),
                1 => new ServerSentEventHubOptions(maxEventBytes: 67108865),
                2 => new ServerSentEventHubOptions(maxReplayEvents: -1),
                3 => new ServerSentEventHubOptions(maxReplayBytes: 0),
                4 => new ServerSentEventHubOptions(maxSubscribers: 0),
                5 => new ServerSentEventHubOptions(maxQueuedEvents: 0),
                6 => new ServerSentEventHubOptions(maxQueuedBytes: 1),
                7 => new ServerSentEventHubOptions(replayWindow: TimeSpan.Zero),
                8 => new ServerSentEventHubOptions(heartbeatInterval: TimeSpan.Zero),
                9 => new ServerSentEventHubOptions(writeTimeout: TimeSpan.Zero),
                10 => new ServerSentEventHubOptions(heartbeatInterval: TimeSpan.FromDays(30)),
                11 => new ServerSentEventHubOptions(writeTimeout: Timeout.InfiniteTimeSpan),
                12 => new ServerSentEventHubOptions(heartbeatInterval: TimeSpan.FromTicks(1)),
                _ => new ServerSentEventHubOptions(writeTimeout: TimeSpan.FromTicks(1))
            });
        }
        [TestCase("")]
        [TestCase("hello")]
        [TestCase("a\nb\r\nc\r")]
        [TestCase("caf\u00e9 \ud83d\ude00")]
        [TestCase("\ndata: not a field\n\n")]
        public async Task LiveSubscribersReceiveTheSameImmutableOrderedEvent(string data)
        {
            using var hub = new ServerSentEventHub();
            hub.Publish("old");
            using var first = hub.Subscribe(); using var second = hub.Subscribe("");
            var id = hub.Publish(data, "progress");
            var a = await first.ReadAsync(); var b = await second.ReadAsync();
            Assert.That(a?.Data, Is.EqualTo(data)); Assert.That(a?.Id, Is.EqualTo(id));
            Assert.That(a, Is.SameAs(b)); Assert.That(a?.EventName, Is.EqualTo("progress"));
            hub.Complete(); Assert.That(await first.ReadAsync(), Is.Null);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReplayToLiveHandoffHasNoGapsOrDuplicates(bool concurrent)
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(maxQueuedEvents: 256));
            var cursor = hub.Publish("0");
            ServerSentEventSubscription? subscription = null;
            var publisher = Task.Run(() => { for (var i = 1; i <= 100; i++) hub.Publish(i.ToString(System.Globalization.CultureInfo.InvariantCulture)); });
            if (!concurrent) await publisher;
            subscription = hub.Subscribe(cursor);
            using (subscription)
            {
                await publisher; hub.Complete();
                var values = new List<string>();
                while (await subscription.ReadAsync() is ServerSentEvent item) values.Add(item.Data);
                Assert.That(values, Is.EqualTo(Enumerable.Range(1, 100).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task SlowSubscriberOverflowIsIsolatedAndSlotRemainsOwned(bool byteBudget)
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(maxEventBytes: 128, maxQueuedEvents: byteBudget ? 8 : 1, maxQueuedBytes: byteBudget ? 128 : 1024, maxSubscribers: 2));
            using var slow = hub.Subscribe(); using var fast = hub.Subscribe();
            hub.Publish(new string('a', 50)); await fast.ReadAsync();
            var last = hub.Publish(new string('b', 50));
            Assert.That((await fast.ReadAsync())?.Id, Is.EqualTo(last));
            await Assert.ThrowsAsync<IOException>(async () => await slow.ReadAsync());
            Assert.That(hub.SubscriberCount, Is.EqualTo(2));
            Assert.That(Assert.Throws<HttpException>(() => hub.Subscribe())?.StatusCode, Is.EqualTo(503));
            slow.Dispose(); slow.Dispose(); Assert.That(hub.SubscriberCount, Is.EqualTo(1));
            using var replacement = hub.Subscribe(); Assert.That(hub.SubscriberCount, Is.EqualTo(2));
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReplayCountAndByteEvictionRequireResynchronization(bool byteBudget)
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(maxEventBytes: 128, maxReplayEvents: byteBudget ? 8 : 1, maxReplayBytes: byteBudget ? 100 : 1024));
            var old = hub.Publish("one"); var previous = hub.Publish("two"); var last = hub.Publish("three");
            Assert.That(Assert.Throws<HttpException>(() => hub.Subscribe(old))?.StatusCode, Is.EqualTo(409));
            using var subscriber = hub.Subscribe(previous);
            Assert.That((await subscriber.ReadAsync())?.Id, Is.EqualTo(last));
        }
        [Test]
        public async Task ReplayExpiryAndForeignEpochDoNotSilentlySkipHistory()
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(replayWindow: TimeSpan.FromMilliseconds(1)));
            var old = hub.Publish("old"); await Task.Delay(20);
            Assert.That(Assert.Throws<HttpException>(() => hub.Subscribe(old))?.StatusCode, Is.EqualTo(409));
            using var other = new ServerSentEventHub(); other.Publish("new");
            Assert.That(Assert.Throws<HttpException>(() => other.Subscribe(old))?.StatusCode, Is.EqualTo(409));
        }
        [TestCase("invalid")]
        [TestCase("")]
        [TestCase("future")]
        [TestCase("canonical")]
        [TestCase("long")]
        public void CursorValidationIsExplicit(string mode)
        {
            using var hub = new ServerSentEventHub(); var id = hub.Publish("one");
            if (mode == "") { using var subscription = hub.Subscribe(""); return; }
            var value = mode switch { "future" => id[..(id.LastIndexOf(':') + 1)] + "999", "canonical" => id[..(id.LastIndexOf(':') + 1)] + "01", "long" => new string('x', 10000), _ => mode };
            Assert.That(Assert.Throws<HttpException>(() => hub.Subscribe(value))?.StatusCode, Is.EqualTo(409));
            Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [Test]
        public void DisabledReplayAndOversizedReplayRecordsDoNotPromiseResume()
        {
            using var disabled = new ServerSentEventHub(new ServerSentEventHubOptions(maxReplayEvents: 0)); var id = disabled.Publish("a");
            Assert.That(Assert.Throws<HttpException>(() => disabled.Subscribe(id))?.StatusCode, Is.EqualTo(409));
            using var tiny = new ServerSentEventHub(new ServerSentEventHubOptions(maxReplayBytes: 1)); id = tiny.Publish("a");
            Assert.That(Assert.Throws<HttpException>(() => tiny.Subscribe(id))?.StatusCode, Is.EqualTo(409));
        }
        [Test]
        public void ReplayMustFitTheSubscriberBudgetWithoutLeakingASlot()
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(maxQueuedEvents: 1));
            var id = hub.Publish("one"); hub.Publish("two"); hub.Publish("three");
            Assert.That(Assert.Throws<HttpException>(() => hub.Subscribe(id))?.StatusCode, Is.EqualTo(409)); Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [TestCase("bad\n")]
        [TestCase("bad\r")]
        [TestCase("bad\0")]
        public void EventFieldsCannotInjectWireFrames(string name)
        {
            using var hub = new ServerSentEventHub(); Assert.Throws<ArgumentException>(() => hub.Publish("x", name));
            Assert.Throws<ArgumentNullException>(() => hub.Publish(null));
            Assert.That(hub.Publish("safe"), Does.EndWith(":1"));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void WireByteBudgetIncludesUnicodeAndLinePrefixes(bool unicode)
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(maxEventBytes: 64));
            Assert.Throws<ArgumentException>(() => hub.Publish(unicode ? new string('\u00e9', 20) : new string('\n', 20)));
            Assert.That(hub.Publish("safe"), Does.EndWith(":1"));
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task CompletionDrainsButDisposalDiscards(bool dispose)
        {
            using var hub = new ServerSentEventHub(); using var subscription = hub.Subscribe(); hub.Publish("queued");
            if (dispose) hub.Dispose(); else hub.Complete();
            Assert.That((await subscription.ReadAsync())?.Data, dispose ? Is.Null : Is.EqualTo("queued"));
            Assert.That(await subscription.ReadAsync(), Is.Null);
            Assert.That(Assert.Throws<HttpException>(() => hub.Subscribe())?.StatusCode, Is.EqualTo(503));
        }
        [Test]
        public async Task CanceledReadsAndConcurrentDisposeDoNotLoseEventsOrSlots()
        {
            using var hub = new ServerSentEventHub(); var subscription = hub.Subscribe();
            using var stop = new CancellationTokenSource(); var pending = subscription.ReadAsync(stop.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await subscription.ReadAsync());
            stop.Cancel(); await Assert.CatchAsync<OperationCanceledException>(async () => await pending);
            hub.Publish("retained"); Assert.That((await subscription.ReadAsync())?.Data, Is.EqualTo("retained"));
            var waiting = subscription.ReadAsync(); await Task.WhenAll(Task.Run(subscription.Dispose), Task.Run(subscription.Dispose));
            Assert.That(await waiting, Is.Null); Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task RealProtocolsFlushReplayLiveEventsAndCompletion(int protocol)
        {
            using var hub = new ServerSentEventHub(); var cursor = hub.Publish("old"); var replay = hub.Publish("replayed");
            await Wire(context => hub.Stream().WriteAsync(context), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/") { Version = client.DefaultRequestVersion, VersionPolicy = client.DefaultVersionPolicy }; request.Headers.Add("Last-Event-ID", cursor);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));
                Assert.That(response.Headers.CacheControl?.NoCache, Is.True); Assert.That(response.Content.Headers.ContentEncoding, Is.Empty);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                Assert.That(await Frame(reader), Is.EqualTo(": connected\n"));
                Assert.That(await Frame(reader), Is.EqualTo("id: " + replay + "\ndata: replayed\n"));
                var live = hub.Publish("a\r\nb\r", "progress");
                Assert.That(await Frame(reader), Is.EqualTo("id: " + live + "\nevent: progress\ndata: a\ndata: b\ndata: \n"));
                hub.Complete(); Assert.That(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
            }, protocol);
            Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [Test]
        public async Task HeartbeatsAreCommentsAndCallerCancellationReleasesTheSlot()
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(heartbeatInterval: TimeSpan.FromMilliseconds(10)));
            using var stop = new CancellationTokenSource();
            await Wire(context => hub.Stream().WriteAsync(context, stop.Token), async client =>
            {
                using var response = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                Assert.That(await Frame(reader), Is.EqualTo(": connected\n"));
                Assert.That(await Frame(reader), Is.EqualTo(": keep-alive\n")); stop.Cancel();
            });
            Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task ControllerResultsKeepOwnedControllersAliveAndBypassSerialization(bool generated, bool task)
        {
            using var hub = new ServerSentEventHub(); var controller = new HubController(hub);
            var module = new WebApiModule("/api", (_, _) => throw new InvalidOperationException("Unexpected serializer."));
            if (generated) module.WithController(HubBindings.HubController, () => controller); else module.WithController(() => controller);
            await using var server = Issues.Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient { BaseAddress = new Uri(server.Url), Timeout = TimeSpan.FromSeconds(10) };
            {
                using var response = await client.GetAsync(task ? "/api/task" : "/api/events", HttpCompletionOption.ResponseHeadersRead);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync()); await Frame(reader);
                Assert.That(controller.Disposed, Is.Zero); var id = hub.Publish("live");
                Assert.That(await Frame(reader), Is.EqualTo("id: " + id + "\ndata: live\n"));
                hub.Complete(); await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await controller.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.That(controller.Disposed, Is.EqualTo(1)); Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [TestCase(204)]
        [TestCase(205)]
        [TestCase(304)]
        public async Task NoEntityResponsesDoNotSubscribe(int status)
        {
            using var hub = new ServerSentEventHub();
            await Wire(context => { context.Response.StatusCode = status; return hub.Stream().WriteAsync(context); }, async client =>
            { using var response = await client.GetAsync("/"); Assert.That((int)response.StatusCode, Is.EqualTo(status)); Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty); });
            Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task UnauthorizedRequestsDoNotSubscribeOrReplay(bool generated)
        {
            using var hub = new ServerSentEventHub(); var cursor = hub.Publish("private");
            var module = new WebApiModule("/api");
            module.ConfigureAuthorization(new AuthorizationOptions());
            if (generated) module.WithController(HubBindings.HubController, () => new HubController(hub));
            else module.WithController(() => new HubController(hub));
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/secure"); request.Headers.Add("Last-Event-ID", cursor);
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(hub.SubscriberCount, Is.Zero);
                Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("private"));
            });
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReplayAndAdmissionFailuresUseOrdinaryHttpErrors(bool full)
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(maxSubscribers: 1));
            using var held = full ? hub.Subscribe() : null;
            await Wire(context => hub.Stream().WriteAsync(context), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/"); request.Headers.Add("Last-Event-ID", "foreign");
                using var response = await client.SendAsync(request); Assert.That((int)response.StatusCode, Is.EqualTo(full ? 503 : 409));
            });
            Assert.That(hub.SubscriberCount, Is.EqualTo(full ? 1 : 0));
        }
        [Test]
        public async Task HeadAndFixedLengthsNeverAcquireASubscription()
        {
            using var hub = new ServerSentEventHub();
            await Wire(context => hub.Stream().WriteAsync(context), async client =>
            {
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
            });
            await Wire(context => { context.Response.ContentLength64 = 0; return hub.Stream().WriteAsync(context); }, async client =>
            { using var response = await client.GetAsync("/"); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError)); });
            Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task WriteDeadlinesFailuresAndCleanupReleaseSubscriptions(int mode)
        {
            using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(writeTimeout: TimeSpan.FromMilliseconds(20)));
            using var stream = new EventFailureStream(mode);
            var response = DispatchProxy.Create<IHttpResponse, Issues.Issue587_ProgressEvents.PropertyProxy>();
            var values = ((Issues.Issue587_ProgressEvents.PropertyProxy)(object)response).Values;
            values["Headers"] = new WebHeaderCollection(); values["OutputStream"] = stream; values["StatusCode"] = 200;
            var request = DispatchProxy.Create<IHttpRequest, Issues.Issue587_ProgressEvents.PropertyProxy>();
            values = ((Issues.Issue587_ProgressEvents.PropertyProxy)(object)request).Values;
            values["Headers"] = new WebHeaderCollection(); values["HttpVerb"] = HttpVerbs.Get;
            var context = DispatchProxy.Create<IHttpContext, Issues.Issue587_ProgressEvents.PropertyProxy>();
            values = ((Issues.Issue587_ProgressEvents.PropertyProxy)(object)context).Values;
            values["Request"] = request; values["Response"] = response; values["CancellationToken"] = CancellationToken.None;
            var writing = hub.Stream().WriteAsync(context);
            if (mode == 3) hub.Complete();
            if (mode == 0) await Assert.CatchAsync<OperationCanceledException>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(2)));
            else if (mode is 1 or 3) await Assert.ThrowsAsync<IOException>(async () => await writing);
            else
            {
                var error = await Assert.ThrowsAsync<AggregateException>(async () => await writing);
                Assert.That(error?.InnerExceptions.Select(item => item.Message), Is.EqualTo(new[] { "write fault", "cleanup fault" }));
            }
            Assert.That(hub.SubscriberCount, Is.Zero);
            stream.FailDisposal = false;
        }
        [Test]
        public async Task ForcedCompressionIsRejectedBeforeSubscribing()
        {
            using var hub = new ServerSentEventHub();
            await Wire(context => hub.Stream().WriteAsync(context), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/"); request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, identity;q=0");
                using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotAcceptable));
            });
            Assert.That(hub.SubscriberCount, Is.Zero);
        }
        [Test]
        public void OpenApiDescribesTheWireMediaAndNotTheHubObject()
        {
            var doc = new OpenApiDocumentBuilder("Events", "1").AddController("/api", HubBindings.HubController).Build(new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });
            Assert.That(doc.ToJson(), Does.Contain("text/event-stream")); Assert.That(doc.ToJson(), Does.Contain("x-embedio-streaming"));
        }
        private sealed class EventFailureStream : MemoryStream
        {
            private readonly int _mode;
            internal bool FailDisposal;
            internal EventFailureStream(int mode) { _mode = mode; FailDisposal = mode is 2 or 3; }
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_mode == 0) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return; }
                if (_mode == 2) throw new IOException("write fault");
                await base.WriteAsync(buffer, offset, count, cancellationToken);
            }
            public override Task FlushAsync(CancellationToken cancellationToken) => _mode == 1 ? Task.FromException(new IOException("flush fault")) : Task.CompletedTask;
            protected override void Dispose(bool disposing) { if (FailDisposal) throw new IOException("cleanup fault"); base.Dispose(disposing); }
        }
        private static async Task<string> Frame(StreamReader reader)
        {
            var lines = new List<string>();
            while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is string line && line.Length > 0) lines.Add(line);
            return string.Join("\n", lines) + "\n";
        }
        private static async Task Wire(Func<IHttpContext, Task> handler, Func<HttpClient, Task> use, int protocol = 1)
        {
            if (protocol == 3 && (!QuicListener.IsSupported || !QuicConnection.IsSupported))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.Fail("QUIC is required by this job.");
                Assert.Ignore("The host does not provide QUIC."); return;
            }
            var url = TestObjects.Resources.GetServerAddress();
            if (protocol > 1) url = url.Replace("http://", "https://", StringComparison.Ordinal);
            using var certificate = protocol > 1 ? HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable) : null;
            using var stop = new CancellationTokenSource();
            using var server = (protocol == 1 ? new WebServer(url)
                : new WebServer(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO, certificate ?? throw new InvalidOperationException("Missing test certificate."), url))
                .WithModule(new ActionModule(context => handler(context)));
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) => { if (change.NewState == WebServerState.Listening) listening.TrySetResult(); };
            var running = server.RunAsync(stop.Token);
            using var transport = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                { RemoteCertificateValidationCallback = (_, peer, _, _) => certificate != null && peer?.GetCertHashString() == certificate.GetCertHashString() }
            };
            try
            {
                using var client = new HttpClient(transport)
                {
                    BaseAddress = new Uri(url),
                    Timeout = TimeSpan.FromSeconds(10),
                    DefaultRequestVersion = protocol == 3 ? HttpVersion.Version30 : protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await use(client);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    [GeneratedControllerBinding(typeof(HubController))]
    internal static partial class HubBindings { }
    public sealed class HubController : WebApiController, IDisposable
    {
        private readonly ServerSentEventHub _hub;
        public int Disposed;
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HubController(ServerSentEventHub hub) => _hub = hub;
        [Route(HttpVerbs.Get, "/events")]
        public ServerSentEventStream Events() => _hub.Stream();
        [Route(HttpVerbs.Get, "/task")]
        public Task<ServerSentEventStream> TaskResult() => Task.FromResult(_hub.Stream());
        [Route(HttpVerbs.Get, "/secure"), Authorize, OpenApiIgnore]
        public ServerSentEventStream Secure() => _hub.Stream();
        public void Dispose() { Interlocked.Increment(ref Disposed); Released.TrySetResult(); }
    }
}
