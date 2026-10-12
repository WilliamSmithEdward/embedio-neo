using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #137, first increment: opt-in HTTP server metrics and spans from the
    // EmbedIO.Server meter and activity source, over real connections.
    [NonParallelizable]
    public class Issue137_Telemetry
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [Test]
        public async Task DisabledByDefaultRecordsNothing()
        {
            using var capture = new Capture();
            var url = Resources.GetServerAddress();
            await using (var host = Host.Start(url, o => { }, server => server.WithAction("/", HttpVerbs.Get, Ok)))
                Assert.That(await host.Client.GetStringAsync(url), Is.EqualTo("ok"));
            var port = new Uri(url).Port;
            Assert.That(capture.Durations.Where(m => Equals(m.Tag("server.port"), port)), Is.Empty);
            Assert.That(capture.Spans.Where(s => Equals(s.GetTagItem("server.port"), port)), Is.Empty);
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task DurationActiveRequestsAndSpanCarryTheRouteTemplate(int protocol)
        {
            using var capture = new Capture();
            var name = Name();
            var url = Resources.GetServerAddress();
            await using (var host = Host.Start(url, o => o.WithTelemetry(name),
                server => server.WithWebApi("/api", m => m.WithController<ItemsController>()), protocol))
            {
                Assert.That(await host.Client.GetStringAsync(url + "api/items/42"), Is.EqualTo("\"item 42\""));
            }

            var duration = capture.Single(name);
            Assert.That(duration.Tag("http.request.method"), Is.EqualTo("GET"));
            Assert.That(duration.Tag("url.scheme"), Is.EqualTo("http"));
            Assert.That(duration.Tag("network.protocol.name"), Is.EqualTo("http"));
            Assert.That(duration.Tag("network.protocol.version"), Is.EqualTo(protocol == 2 ? "2" : "1.1"));
            Assert.That(duration.Tag("http.response.status_code"), Is.EqualTo(200));
            Assert.That(duration.Tag("http.route"), Is.EqualTo("/api/items/{id}"), "The template, not the requested path.");
            Assert.That(duration.Tag("error.type"), Is.Null);
            Assert.That(duration.Tag("server.port"), Is.EqualTo(new Uri(url).Port));
            Assert.That(duration.Value, Is.GreaterThan(0));

            var active = capture.Active.Where(m => Equals(m.Tag("embedio.server.name"), name)).ToList();
            Assert.That(active.Select(m => m.Value), Is.EqualTo(new double[] { 1, -1 }), "Incremented at start and decremented at completion.");

            var span = capture.Spans.Single(s => Equals(s.GetTagItem("embedio.server.name"), name));
            Assert.That(span.Kind, Is.EqualTo(ActivityKind.Server));
            Assert.That(span.DisplayName, Is.EqualTo("GET /api/items/{id}"));
            Assert.That(span.GetTagItem("url.path"), Is.EqualTo("/api/items/42"));
            Assert.That(span.GetTagItem("http.response.status_code"), Is.EqualTo(200));
            Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Unset));
        }

        [Test]
        public async Task InboundTraceContextBecomesTheParentAndFlowsToHandlers()
        {
            using var capture = new Capture();
            var name = Name();
            var url = Resources.GetServerAddress();
            string? handlerTrace = null;
            await using (var host = Host.Start(url, o => o.WithTelemetry(name), server => server.WithAction("/", HttpVerbs.Get, context =>
            {
                handlerTrace = Activity.Current?.TraceId.ToHexString();
                return Ok(context);
            })))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
                request.Headers.Add("tracestate", "vendor=value");
                using var response = await host.Client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }

            capture.WaitFor(name, 1);
            var span = capture.Spans.Single(s => Equals(s.GetTagItem("embedio.server.name"), name));
            Assert.That(span.TraceId.ToHexString(), Is.EqualTo("0af7651916cd43dd8448eb211c80319c"));
            Assert.That(span.ParentSpanId.ToHexString(), Is.EqualTo("b7ad6b7169203331"));
            Assert.That(span.HasRemoteParent, Is.True);
            Assert.That(span.TraceStateString, Is.EqualTo("vendor=value"));
            Assert.That(handlerTrace, Is.EqualTo(span.TraceId.ToHexString()), "Activity.Current inside the handler is the server span.");
        }

        [Test]
        public async Task HandlerExceptionRecordsItsTypeAndAnErrorSpan()
        {
            using var capture = new Capture();
            var name = Name();
            var url = Resources.GetServerAddress();
            await using (var host = Host.Start(url, o => o.WithTelemetry(name),
                server => server.WithAction("/", HttpVerbs.Get, _ => throw new InvalidOperationException("boom"))))
            {
                using var response = await host.Client.GetAsync(url);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            }
            var duration = capture.Single(name);
            Assert.That(duration.Tag("error.type"), Is.EqualTo("System.InvalidOperationException"));
            Assert.That(duration.Tag("http.response.status_code"), Is.EqualTo(500));
            Assert.That(capture.Spans.Single(s => Equals(s.GetTagItem("embedio.server.name"), name)).Status, Is.EqualTo(ActivityStatusCode.Error));
        }

        [Test]
        public async Task HttpExceptionsUseTheirStatusCode()
        {
            using var capture = new Capture();
            var name = Name();
            var url = Resources.GetServerAddress();
            await using (var host = Host.Start(url, o => o.WithTelemetry(name), server => server.WithAction("/", HttpVerbs.Get, context =>
                context.Request.Url.AbsolutePath == "/busy" ? throw new HttpException(503) : throw HttpException.Forbidden())))
            {
                using (var forbidden = await host.Client.GetAsync(url)) Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                using (var busy = await host.Client.GetAsync(url + "busy")) Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            }
            capture.WaitFor(name, 2);
            var durations = capture.Durations.Where(m => Equals(m.Tag("embedio.server.name"), name)).ToList();
            Assert.That(durations.Single(m => Equals(m.Tag("http.response.status_code"), 403)).Tag("error.type"), Is.Null, "A 4xx response is not a server error.");
            Assert.That(durations.Single(m => Equals(m.Tag("http.response.status_code"), 503)).Tag("error.type"), Is.EqualTo("503"));
        }

        [Test]
        public async Task HandlerStoppedByTheDeadlineRecordsItsCancellation()
        {
            using var capture = new Capture();
            var name = Name();
            var url = Resources.GetServerAddress();
            await using (var host = Host.Start(url, o => o.WithTelemetry(name).WithRequestTimeout(TimeSpan.FromMilliseconds(200)),
                server => server.WithAction("/", HttpVerbs.Get, async context => await Task.Delay(Timeout.Infinite, context.CancellationToken))))
            {
                using var response = await host.Client.GetAsync(url);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            }
            var duration = capture.Single(name);
            Assert.That(duration.Tag("http.response.status_code"), Is.EqualTo(503));
            Assert.That(duration.Tag("error.type"), Is.EqualTo("System.Threading.Tasks.TaskCanceledException"));
            Assert.That(duration.Value, Is.GreaterThanOrEqualTo(0.15));
        }

        [Test]
        public async Task UnmatchedRequestsHaveNoRouteAndUnknownMethodsAreOther()
        {
            using var capture = new Capture();
            var name = Name();
            var url = Resources.GetServerAddress();
            await using (var host = Host.Start(url, o => o.WithTelemetry(name), server => server.WithModule(new RoutingModule("/items").OnGet("/{id}", (context, _) => Ok(context)))))
            {
                using (var missing = await host.Client.GetAsync(url + "nothing")) Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url + "items/1");
                using var other = await host.Client.SendAsync(request);
            }
            capture.WaitFor(name, 2);
            var durations = capture.Durations.Where(m => Equals(m.Tag("embedio.server.name"), name)).ToList();
            var notFound = durations.Single(m => Equals(m.Tag("http.response.status_code"), 404) && Equals(m.Tag("http.request.method"), "GET"));
            Assert.That(notFound.Tag("http.route"), Is.Null);
            Assert.That(durations.Any(m => Equals(m.Tag("http.request.method"), "_OTHER")), Is.True, "PROPFIND is reported as _OTHER.");
        }

        [Test]
        public async Task NestedModuleGroupsComposeTheTemplate()
        {
            using var capture = new Capture();
            var name = Name();
            var url = Resources.GetServerAddress();
            await using (var host = Host.Start(url, o => o.WithTelemetry(name), server => server.WithModule(
                new ModuleGroup("/v1", true).WithModule(new RoutingModule("/items").OnGet("/{id}", (context, _) => Ok(context))))))
            {
                Assert.That(await host.Client.GetStringAsync(url + "v1/items/7"), Is.EqualTo("ok"));
            }
            Assert.That(capture.Single(name).Tag("http.route"), Is.EqualTo("/v1/items/{id}"));
        }

        [Test]
        public async Task StreamedResponsesAreMeasuredThroughCompletion()
        {
            using var capture = new Capture();
            var name = Name();
            var url = Resources.GetServerAddress();
            await using (var host = Host.Start(url, o => o.WithTelemetry(name), server => server.WithAction("/", HttpVerbs.Get, async context =>
            {
                using var stream = context.OpenResponseStream();
                await stream.WriteAsync(new byte[] { 1 }, 0, 1);
                await stream.FlushAsync();
                await Task.Delay(400);
                await stream.WriteAsync(new byte[] { 2 }, 0, 1);
            })))
            {
                Assert.That(await host.Client.GetByteArrayAsync(url), Is.EqualTo(new byte[] { 1, 2 }));
            }
            Assert.That(capture.Single(name).Value, Is.GreaterThanOrEqualTo(0.35));
        }

        [Test]
        public async Task IndependentServersAreDistinguishedByName()
        {
            using var capture = new Capture();
            string first = Name(), second = Name();
            string firstUrl = Resources.GetServerAddress(), secondUrl = Resources.GetServerAddress();
            await using (var a = Host.Start(firstUrl, o => o.WithTelemetry(first), server => server.WithAction("/", HttpVerbs.Get, Ok)))
            await using (var b = Host.Start(secondUrl, o => o.WithTelemetry(second), server => server.WithAction("/", HttpVerbs.Get, Ok)))
            {
                await a.Client.GetStringAsync(firstUrl);
                await b.Client.GetStringAsync(secondUrl);
                await b.Client.GetStringAsync(secondUrl);
            }
            capture.WaitFor(first, 1);
            capture.WaitFor(second, 2);
            Assert.That(capture.Durations.Count(m => Equals(m.Tag("embedio.server.name"), first)), Is.EqualTo(1));
            Assert.That(capture.Durations.Count(m => Equals(m.Tag("embedio.server.name"), second)), Is.EqualTo(2));
        }

        [Test]
        public void OptionsDefaultsValidationAndLocking()
        {
            var options = new WebServerOptions();
            Assert.That(options.EnableTelemetry, Is.False);
            Assert.That(options.TelemetryServerName, Is.Null);
            Assert.That(() => options.TelemetryServerName = " ", Throws.ArgumentException);
            options.WithUrlPrefix(Resources.GetServerAddress()).WithTelemetry("api");
            Assert.That(options.EnableTelemetry, Is.True);
            Assert.That(options.TelemetryServerName, Is.EqualTo("api"));
            using var server = new WebServer(options);
            Assert.That(() => options.EnableTelemetry = false, Throws.InvalidOperationException);
            Assert.That(() => options.TelemetryServerName = "other", Throws.InvalidOperationException);
        }

        // ---------------------------------------------------------------- helpers

        private static string Name() => "test-" + Guid.NewGuid().ToString("N");

        private static Task Ok(IHttpContext context) => context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);

        public sealed class ItemsController : WebApiController
        {
            [Route(HttpVerbs.Get, "/items/{id}")]
            public string GetItem(int id) => "item " + id;
        }

        private sealed record Measurement(double Value, KeyValuePair<string, object?>[] Tags)
        {
            public object? Tag(string name) => Tags.FirstOrDefault(t => t.Key == name).Value;
        }

        // Collects EmbedIO.Server measurements and stopped spans for the duration of a test.
        private sealed class Capture : IDisposable
        {
            private readonly MeterListener _meters = new();
            private readonly ActivityListener _spans;
            public ConcurrentQueue<Measurement> Durations { get; } = new();
            public ConcurrentQueue<Measurement> Active { get; } = new();
            public ConcurrentQueue<Activity> Spans { get; } = new();

            public Capture()
            {
                _meters.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "EmbedIO.Server") listener.EnableMeasurementEvents(instrument);
                };
                _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                {
                    if (instrument.Name == "http.server.request.duration") Durations.Enqueue(new Measurement(value, tags.ToArray()));
                });
                _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                {
                    if (instrument.Name == "http.server.active_requests") Active.Enqueue(new Measurement(value, tags.ToArray()));
                });
                _meters.Start();
                _spans = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == "EmbedIO.Server",
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = activity => Spans.Enqueue(activity),
                };
                ActivitySource.AddActivityListener(_spans);
            }

            public Measurement Single(string serverName)
            {
                WaitFor(serverName, 1);
                return Durations.Single(m => Equals(m.Tag("embedio.server.name"), serverName));
            }

            // The client can read a response before the server records it: recording follows
            // the context's close. Wait for the expected measurements and spans.
            public void WaitFor(string serverName, int requests)
            {
                var stop = DateTime.UtcNow + Deadline;
                while (DateTime.UtcNow < stop
                    && (Durations.Count(m => Equals(m.Tag("embedio.server.name"), serverName)) < requests
                        || Active.Count(m => Equals(m.Tag("embedio.server.name"), serverName)) < 2 * requests
                        || (Spans.Count(s => Equals(s.GetTagItem("embedio.server.name"), serverName)) < requests)))
                    Thread.Sleep(10);
            }

            public void Dispose()
            {
                _meters.Dispose();
                _spans.Dispose();
            }
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;
            private Host(WebServer server, HttpClient client) { _server = server; Client = client; _running = server.RunAsync(_stop.Token); }
            public HttpClient Client { get; }

            public static Host Start(string url, Action<WebServerOptions> configure, Action<WebServer> modules, int protocol = 1)
            {
                var server = new WebServer(o => configure(o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)));
                modules(server);
                var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                {
                    DefaultRequestVersion = protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Timeout = Deadline,
                };
                return new Host(server, client);
            }

            // Stopping the server waits for its requests, so every measurement has been recorded.
            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                _stop.Cancel();
                try { await _running.WaitAsync(Deadline); }
                finally { _server.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
