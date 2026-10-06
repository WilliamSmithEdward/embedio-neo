using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue570_ControllerConcurrency
    {
        [TestCase(HttpListenerMode.EmbedIO, "returned")]
        [TestCase(HttpListenerMode.Microsoft, "returned")]
        [TestCase(HttpListenerMode.EmbedIO, "manual")]
        [TestCase(HttpListenerMode.Microsoft, "manual")]
        [TestCase(HttpListenerMode.EmbedIO, "raw-returned")]
        [TestCase(HttpListenerMode.Microsoft, "raw-returned")]
        [TestCase(HttpListenerMode.EmbedIO, "raw-manual")]
        [TestCase(HttpListenerMode.Microsoft, "raw-manual")]
        public async Task OverlappingAwaitedRequestsKeepDistinctContextsAndExactResponses(HttpListenerMode mode, string shape)
        {
            var probe = new Batch(12);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/json", m => m.WithController(() => new ConcurrentController(probe)))
                .WithWebApi("/binary", ResponseSerializer.None(false), m => m.WithController(() => new ConcurrentController(probe)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                var requests = Enumerable.Range(0, 12).Select(async id =>
                {
                    using var input = new StringContent(JsonSerializer.Serialize(new Payload { Id = id }), Encoding.UTF8, MimeType.Json);
                    using var response = shape.StartsWith("raw-", StringComparison.Ordinal)
                        ? await client.GetAsync(url + "binary/" + shape + "/" + id)
                        : await client.PostAsync(url + "json/" + shape, input);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    if (shape.StartsWith("raw-", StringComparison.Ordinal))
                    {
                        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo(MimeType.Default));
                        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Encoding.UTF8.GetBytes("binary:" + id)));
                    }
                    else
                    {
                        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        Assert.That(data.RootElement.GetProperty("Id").GetInt32(), Is.EqualTo(id));
                    }
                }).ToArray();
                await probe.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(probe.Contexts.Count, Is.EqualTo(12));
                Assert.That(probe.Contexts.Values.Distinct().Count(), Is.EqualTo(12));
                probe.Release.TrySetResult();
                await Task.WhenAll(requests);
                using var health = await client.GetAsync(url + "json/health");
                Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
            finally { probe.Release.TrySetResult(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task ManualWritePlusReturnedValueAttemptsASecondResponseButKeepsOtherRequestsUsable(HttpListenerMode mode)
        {
            var attempted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/", async (context, value) =>
                {
                    try { await ResponseSerializer.Json(context, value); attempted.TrySetResult(null); }
                    catch (Exception error) { attempted.TrySetResult(error); throw; }
                }, m => m.WithController<InvalidController>());
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 1 }) { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                using var first = await client.GetAsync(url + "double");
                Assert.That(await first.Content.ReadAsStringAsync(), Does.Contain("first"));
                var error = await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Native implementations may ignore writes to the closed response.
                if (mode == HttpListenerMode.EmbedIO || error != null)
                    Assert.That(error, Is.InstanceOf<ObjectDisposedException>().Or.InstanceOf<InvalidOperationException>());
                using var good = await client.PostAsync(url + "good", new StringContent("{}"));
                Assert.That(good.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await good.Content.ReadAsStringAsync(), Does.Contain("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task RequestAwareActivationKeepsDisposableResourcesAliveUntilAsyncSerializationCompletes(HttpListenerMode mode)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controller = new OwnedController(entered, release, disposed);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/", m => m.RegisterControllerWithContext(typeof(OwnedController), _ => controller,
                    (_, instance) => { ((IDisposable)instance).Dispose(); return Task.CompletedTask; }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                var request = client.GetAsync(url + "owned");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(controller.Disposed, Is.False);
                release.TrySetResult();
                using var response = await request;
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("\"alive\""));
                await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(controller.DisposeCount, Is.EqualTo(1));
            }
            finally { release.TrySetResult(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        public sealed class Batch(int count)
        {
            public ConcurrentDictionary<int, IHttpContext> Contexts { get; } = new();
            public TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task Wait(IHttpContext context, int id)
            {
                Assert.That(Contexts.TryAdd(id, context), Is.True);
                if (Contexts.Count == count) AllEntered.TrySetResult();
                await Release.Task;
                Assert.That(Contexts[id], Is.SameAs(context));
            }
        }
        public sealed class Payload { public int Id { get; set; } }
        public sealed class ConcurrentController(Batch probe) : WebApiController
        {
            [Route(HttpVerbs.Post, "/returned")]
            public async Task<Payload> Returned([JsonData] Payload input) { await probe.Wait(HttpContext, input.Id); return input; }
            [Route(HttpVerbs.Post, "/manual")]
            public async Task Manual([JsonData] Payload input) { await probe.Wait(HttpContext, input.Id); await HttpContext.SendDataAsync(input); }
            [Route(HttpVerbs.Get, "/raw-returned/{id}")]
            public async Task<byte[]> RawReturned(int id) { await probe.Wait(HttpContext, id); Response.ContentType = MimeType.Default; return Encoding.UTF8.GetBytes("binary:" + id); }
            [Route(HttpVerbs.Get, "/raw-manual/{id}")]
            public async Task RawManual(int id) { await probe.Wait(HttpContext, id); Response.ContentType = MimeType.Default; await HttpContext.SendDataAsync(ResponseSerializer.None(false), Encoding.UTF8.GetBytes("binary:" + id)); }
            [Route(HttpVerbs.Get, "/health")]
            public Payload Health() => new() { Id = 42 };
        }
        public sealed class InvalidController : WebApiController
        {
            [Route(HttpVerbs.Get, "/double")]
            public async Task<object> Double() { await HttpContext.SendDataAsync(new { Value = "first" }); return new { Value = "second" }; }
            [Route(HttpVerbs.Post, "/good")]
            public object Good() => new { Value = "healthy" };
        }
        public sealed class OwnedController(TaskCompletionSource entered, TaskCompletionSource release, TaskCompletionSource disposed) : WebApiController, IDisposable
        {
            public bool Disposed { get; private set; }
            public int DisposeCount { get; private set; }
            [Route(HttpVerbs.Get, "/owned")]
            public async Task<string> Get()
            {
                entered.TrySetResult();
                await release.Task;
                if (Disposed) throw new ObjectDisposedException(nameof(OwnedController));
                return "alive";
            }
            public void Dispose() { Disposed = true; DisposeCount++; disposed.TrySetResult(); }
        }
    }
}
