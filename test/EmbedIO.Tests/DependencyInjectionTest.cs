using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.DependencyInjection;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class DependencyInjectionTest
    {
        private static ServiceProvider Build(Probe probe)
            => new ServiceCollection().AddSingleton(probe).AddScoped<RequestService>()
                .AddSingleton<SharedService>().AddEmbedIORequestContext()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        [Test]
        public async Task ConcurrentRequestsShareServicesWithinRequestAndKeepSeparateContexts()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithModule(new CaptureModule())
                .WithWebApi("/api", module => module.WithControllerServices<InjectedController>()), async client =>
                {
                    var first = client.GetStringAsync("/api/wait?name=first");
                    var second = client.GetStringAsync("/api/wait?name=second");
                    await probe.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var requests = probe.Requests.ToArray();
                    Assert.That(requests.Length, Is.EqualTo(2));
                    Assert.That(requests[0].Id, Is.Not.EqualTo(requests[1].Id));
                    Assert.That(requests.All(service => service.DisposeCount == 0), Is.True);
                    probe.Release.TrySetResult(true);
                    var replies = await Task.WhenAll(first, second);
                    Assert.That(replies, Is.EquivalentTo(new[] { "\"first\"", "\"second\"" }));
                    Assert.That(probe.SharedInstances.Distinct().Count(), Is.EqualTo(1));
                    Assert.That(probe.ControllerDisposeCount, Is.EqualTo(2));
                    Assert.That(requests.All(service => service.DisposeCount == 1), Is.True);
                });
            Assert.That(provider.GetRequiredService<SharedService>().Disposed, Is.False);
        }

        [TestCase("sync-failure")]
        [TestCase("async-failure")]
        [TestCase("binding-failure")]
        public async Task FailedRequestsKeepScopeForErrorHandlerAndDisposeOnce(string route)
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server =>
            {
                server.WithDependencyInjection(provider).WithWebApi("/api", module => module.WithControllerServices<InjectedController>());
                server.OnUnhandledException = async (context, _) =>
                {
                    var scoped = context.GetRequestServices().GetRequiredService<RequestService>();
                    Assert.That(scoped.DisposeCount, Is.Zero);
                    await context.SendStringAsync("handled", "text/plain", WebServer.DefaultEncoding);
                };
            }, async client =>
            {
                var response = await client.GetAsync("/api/" + route);
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("handled"));
                Assert.That(probe.ControllerDisposeCount, Is.EqualTo(1));
                Assert.That(probe.Requests.Single().DisposeCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SerializerCanAwaitAndReadLiveControllerDependencies()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", async (context, value) =>
                {
                    var scoped = context.GetRequestServices().GetRequiredService<RequestService>();
                    await Task.Yield();
                    Assert.That(scoped.DisposeCount, Is.Zero);
                    Assert.That(probe.ControllerDisposeCount, Is.Zero);
                    await context.SendStringAsync((string)value!, "text/plain", WebServer.DefaultEncoding);
                }, module => module.WithControllerServices<InjectedController>()), async client =>
                {
                    Assert.That(await client.GetStringAsync("/api/value"), Is.EqualTo("value"));
                    Assert.That(probe.ControllerDisposeCount, Is.EqualTo(1));
                    Assert.That(probe.Requests.Single().DisposeCount, Is.EqualTo(1));
                });
        }

        [Test]
        public async Task SerializerFailureStillReleasesControllerAndScope()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", (_, _) => Task.FromException(new InvalidOperationException("serializer")),
                    module => module.WithControllerServices<InjectedController>()), async client =>
                    {
                        Assert.That((await client.GetAsync("/api/value")).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                        Assert.That(probe.ControllerDisposeCount, Is.EqualTo(1));
                        Assert.That(probe.Requests.Single().DisposeCount, Is.EqualTo(1));
                    });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AsyncCleanupFinishesBeforeResponseCompletesAndRunsInOwnershipOrder(bool throwOnDispose)
        {
            var probe = new Probe { ThrowOnDispose = throwOnDispose };
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", module => module.WithControllerServices<AsyncController>()), async client =>
                {
                    var response = await client.GetAsync("/api/async");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(probe.Events.ToArray(), Is.EqualTo(new[] { "controller", "scope" }));
                    Assert.That(probe.Requests.Single().DisposeCount, Is.EqualTo(1));
                });
        }

        [Test]
        public async Task RuntimeRegistrationAndExplicitServiceArgumentsUseRequestProvider()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", module => module.WithControllerServices(typeof(InjectedController))), async client =>
                {
                    Assert.That(await client.GetStringAsync("/api/argument"), Is.EqualTo("true"));
                });
        }

        [Test]
        public async Task ControllerRegistrationsAreNotResolvedOrDoubleDisposed()
        {
            var probe = new Probe();
            var services = new ServiceCollection().AddSingleton(probe).AddScoped<RequestService>()
                .AddSingleton<SharedService>().AddEmbedIORequestContext();
            services.AddSingleton<InjectedController>(_ => throw new Exception("Must not resolve registered controller"));
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", module => module.WithControllerServices<InjectedController>()), async client =>
                {
                    await client.GetAsync("/api/value");
                    Assert.That(probe.ControllerDisposeCount, Is.EqualTo(1));
                });
            await provider.DisposeAsync();
            Assert.That(probe.ControllerDisposeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ConstructorFailureDisposesDependenciesAlreadyResolved()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", module => module.WithControllerServices<ThrowingConstructorController>()), async client =>
                {
                    Assert.That((await client.GetAsync("/api/value")).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                    Assert.That(probe.Requests.Single().DisposeCount, Is.EqualTo(1));
                });
        }

        [Test]
        public async Task MissingDependencyReturnsErrorAndDoesNotPoisonLaterRequests()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", module => module.WithControllerServices<MissingDependencyController>()
                    .WithControllerServices<InjectedController>()), async client =>
                    {
                        Assert.That((await client.GetAsync("/api/missing")).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                        Assert.That((await client.GetAsync("/api/value")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    });
        }

        [Test]
        public void ConfigurationRejectsDuplicateMiddlewareAndInvalidControllerTypes()
        {
            var probe = new Probe();
            using var provider = Build(probe);
            using var server = new TestWebServer();
            server.WithDependencyInjection(provider);
            Assert.Throws<InvalidOperationException>(() => server.WithDependencyInjection(provider));
            var module = new WebApiModule("/");
            Assert.Throws<ArgumentException>(() => module.WithControllerServices(typeof(WebApiController)));
            Assert.Throws<ArgumentException>(() => module.WithControllerServices(typeof(string)));
            module.WithControllerServices<InjectedController>();
            Assert.Throws<ArgumentException>(() => module.WithControllerServices<InjectedController>());
        }

        [Test]
        public async Task WrongMiddlewareOrderReportsMissingRequestServices()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithWebApi("/api", module => module.WithControllerServices<InjectedController>())
                .WithDependencyInjection(provider), async client =>
                {
                    Assert.That((await client.GetAsync("/api/value")).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                    Assert.That(probe.Requests, Is.Empty);
                });
        }

        [Test]
        public void ContextCannotBeResolvedAtRootOrOutsideAnHttpRequest()
        {
            var probe = new Probe();
            using var provider = Build(probe);
            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IHttpContext>());
            using var scope = provider.CreateScope();
            Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IHttpContext>());
        }

        [Test]
        public async Task ContextFactoryReleaseIsAwaitedAfterAsynchronousWork()
        {
            var probe = new Probe();
            var controller = new ManualController(probe);
            await TestWebServer.UseAsync(server => server.WithWebApi("/api", module =>
                module.RegisterControllerWithContext(typeof(ManualController), _ => controller, async (_, instance) =>
                {
                    Assert.That(instance, Is.SameAs(controller));
                    await Task.Yield();
                    probe.Events.Enqueue("release");
                })), async client =>
                {
                    var request = client.GetAsync("/api/manual");
                    await probe.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(probe.Events, Is.Empty);
                    probe.Release.TrySetResult(true);
                    await request;
                    Assert.That(probe.Events.ToArray(), Is.EqualTo(new[] { "action", "release" }));
                });
        }

        [Test]
        public async Task CleanupCallbacksAwaitInReverseOrderAndContinueAfterFailure()
        {
            var events = new ConcurrentQueue<int>();
            await TestWebServer.UseAsync(server => server.WithAction("/", HttpVerbs.Any, context =>
            {
                context.OnRequestCompleted(() => { events.Enqueue(1); return Task.CompletedTask; });
                context.OnRequestCompleted(() => Task.FromException(new Exception("cleanup")));
                context.OnRequestCompleted(async () => { await Task.Yield(); events.Enqueue(3); });
                return context.SendStringAsync("ok", "text/plain", WebServer.DefaultEncoding);
            }), async client =>
            {
                await client.GetAsync("/");
                Assert.That(events.ToArray(), Is.EqualTo(new[] { 3, 1 }));
            });
        }

        [Test]
        public async Task CancelledRequestStillReleasesControllerAndScope()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", module => module.WithControllerServices<InjectedController>()), async client =>
                {
                    using var cancel = new CancellationTokenSource();
                    var request = client.GetAsync("/api/wait?name=cancelled", cancel.Token);
                    await probe.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    cancel.Cancel();
                    probe.Release.TrySetResult(true);
                    await Assert.ThatAsync(async () => await request, Throws.InstanceOf<OperationCanceledException>());
                    Assert.That(probe.ControllerDisposeCount, Is.EqualTo(1));
                    Assert.That(probe.Requests.Single().DisposeCount, Is.EqualTo(1));
                });
        }

        [Test]
        public async Task FlushFailureStillReleasesScope()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithAction("/", HttpVerbs.Any, context =>
                {
                    context.GetRequestServices().GetRequiredService<RequestService>();
                    context.Response.OutputStream.Dispose();
                    return Task.CompletedTask;
                }), async client =>
                {
                    await client.GetAsync("/");
                    Assert.That(probe.Requests.Single().DisposeCount, Is.EqualTo(1));
                });
        }

        [Test]
        public async Task AsyncDisposalWinsWhenControllerImplementsBothInterfaces()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithWebApi("/api", module => module.WithControllerServices<DualDisposableController>()), async client =>
                {
                    await client.GetAsync("/api/dual");
                    Assert.That(probe.Events.ToArray(), Is.EqualTo(new[] { "async" }));
                });
        }

        [Test]
        public async Task NullAndWrongFactoryResultsProduceErrorsAndReleaseReturnedInstances()
        {
            var released = 0;
            await TestWebServer.UseAsync(server => server.WithWebApi("/api", module =>
            {
                module.RegisterControllerWithContext(typeof(ManualController), _ => null!, (_, _) => Task.CompletedTask);
                module.RegisterControllerWithContext(typeof(DualDisposableController), _ => new ManualController(new Probe()), (_, _) =>
                {
                    released++;
                    return Task.CompletedTask;
                });
            }), async client =>
            {
                Assert.That((await client.GetAsync("/api/manual")).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That((await client.GetAsync("/api/dual")).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(released, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task LegacyFactoryKeepsItsExistingDisposalBehavior()
        {
            var probe = new Probe();
            var controller = new LegacyController(probe);
            await TestWebServer.UseAsync(server => server.WithWebApi("/api", module => module.WithController(() => controller)), async client =>
            {
                var request = client.GetAsync("/api/legacy");
                await probe.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(controller.Disposed, Is.True);
                probe.Release.TrySetResult(true);
                await request;
            });
        }

        [Test]
        public async Task ScopeIsReleasedForNotFoundAndMethodNotAllowedResponses()
        {
            var probe = new Probe();
            await using var provider = Build(probe);
            await TestWebServer.UseAsync(server => server.WithDependencyInjection(provider)
                .WithModule(new CaptureModule())
                .WithWebApi("/api", module => module.WithControllerServices<InjectedController>()), async client =>
                {
                    Assert.That((await client.GetAsync("/absent")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                    Assert.That((await client.PostAsync("/api/value", null)).StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
                    Assert.That(probe.Requests.Count, Is.EqualTo(2));
                    Assert.That(probe.Requests.All(service => service.DisposeCount == 1), Is.True);
                });
        }
        private static ServiceProvider BuildHost(Probe probe, string url, Action<WebServer>? configure = null)
            => new ServiceCollection().AddSingleton(probe).AddScoped<RequestService>()
                .AddSingleton<SharedService>().AddSingleton<IHostApplicationLifetime, TestLifetime>()
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddEmbedIO(options => options.WithUrlPrefix(url), (server, _) =>
                {
                    server.WithWebApi("/api", module => module.WithControllerServices<InjectedController>());
                    configure?.Invoke(server);
                }).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        [Test]
        public async Task HostedServerStartsServesAndReleasesPortWithoutOwningRootServices()
        {
            var probe = new Probe();
            var url = Resources.GetServerAddress();
            await using var provider = BuildHost(probe, url);
            var host = provider.GetRequiredService<IHostedService>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StartAsync(timeout.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                Assert.That(await client.GetStringAsync(url + "api/value"), Is.EqualTo("\"value\""));
            }
            finally { await host.StopAsync(timeout.Token); }
            Assert.That(provider.GetRequiredService<SharedService>().Disposed, Is.False);
            using var replacement = new WebServer(url);
            var running = replacement.RunAsync(timeout.Token);
            Assert.That(replacement.State, Is.EqualTo(WebServerState.Listening));
            timeout.Cancel();
            await running;
        }

        [Test]
        public async Task HostedShutdownWaitsForActiveRequestCleanup()
        {
            var probe = new Probe();
            var url = Resources.GetServerAddress();
            await using var provider = BuildHost(probe, url);
            var host = provider.GetRequiredService<IHostedService>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StartAsync(timeout.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var request = client.GetAsync(url + "api/wait?name=stopping");
            await probe.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped = host.StopAsync(timeout.Token);
            try
            {
                Assert.That(stopped.IsCompleted, Is.False);
                Assert.That(probe.Requests.Single().DisposeCount, Is.Zero);
            }
            finally { probe.Release.TrySetResult(true); }
            await stopped;
            try { await request; }
            catch (HttpRequestException) { /* Listener cancellation may close the response. */ }
            Assert.That(probe.Requests.Single().DisposeCount, Is.EqualTo(1));
            Assert.That(probe.ControllerDisposeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task HostedStartupFailureIsPropagated()
        {
            var probe = new Probe();
            var url = Resources.GetServerAddress();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var provider = BuildHost(probe, url, server => server.WithModule(new FailingStartModule()));
            var host = provider.GetRequiredService<IHostedService>();
            try
            {
                await Assert.ThatAsync(() => host.StartAsync(timeout.Token), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("startup"));
                await Assert.ThatAsync(() => host.StopAsync(timeout.Token), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("startup"));
            }
            finally
            {
                try { await provider.DisposeAsync(); }
                catch (Exception) { /* Disposal observes the same startup failure. */ }
            }
        }

        [Test]
        public async Task ConcurrentHostedStopCallsShareOneShutdownAndReleaseModulesOnce()
        {
            var probe = new Probe();
            var owned = new OwnedModule();
            await using var provider = BuildHost(probe, Resources.GetServerAddress(), server => server.WithModule(owned));
            var host = provider.GetRequiredService<IHostedService>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StartAsync(timeout.Token);
            await Task.WhenAll(host.StopAsync(timeout.Token), host.StopAsync(timeout.Token));
            await ((IAsyncDisposable)host).DisposeAsync();
            Assert.That(owned.DisposeCount, Is.EqualTo(1));
        }
        public sealed class Probe
        {
            public ConcurrentQueue<RequestService> Requests { get; } = new();
            public ConcurrentQueue<SharedService> SharedInstances { get; } = new();
            public ConcurrentQueue<string> Events { get; } = new();
            public TaskCompletionSource<bool> FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Entered;
            public int ControllerDisposeCount;
            public bool ThrowOnDispose;
        }

        public sealed class RequestService : IAsyncDisposable
        {
            private readonly Probe _probe;
            public RequestService(Probe probe, IHttpContext context) { _probe = probe; Context = context; probe.Requests.Enqueue(this); }
            public IHttpContext Context { get; }
            public Guid Id { get; } = Guid.NewGuid();
            public int DisposeCount { get; private set; }
            public async ValueTask DisposeAsync() { await Task.Yield(); DisposeCount++; _probe.Events.Enqueue("scope"); }
        }

        public sealed class SharedService : IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        private sealed class CaptureModule : WebModuleBase
        {
            public CaptureModule() : base("/") { }
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context)
            {
                context.Items[typeof(RequestService)] = context.GetRequestServices().GetRequiredService<RequestService>();
                return Task.CompletedTask;
            }
        }

        public sealed class InjectedController : WebApiController, IDisposable
        {
            private readonly Probe _probe;
            private readonly RequestService _request;
            private readonly SharedService _shared;
            public InjectedController(Probe probe, RequestService request, SharedService shared)
            { _probe = probe; _request = request; _shared = shared; }
            [Route(HttpVerbs.Get, "/value")] public string Value() => "value";
            [Route(HttpVerbs.Get, "/argument")] public bool Argument([FromServices] RequestService service) => ReferenceEquals(service, _request);
            [Route(HttpVerbs.Get, "/sync-failure")] public string Fail() => throw new InvalidOperationException("sync");
            [Route(HttpVerbs.Get, "/async-failure")] public async Task<string> FailAsync() { await Task.Yield(); throw new InvalidOperationException("async"); }
            [Route(HttpVerbs.Get, "/binding-failure")] public string FailBinding([JsonData] Payload data) => data.Text;
            [Route(HttpVerbs.Get, "/wait")]
            public async Task<string> Wait([QueryField] string name)
            {
                Assert.That(_request.Context, Is.SameAs(HttpContext));
                if (HttpContext.Items.TryGetValue(typeof(RequestService), out var captured)) Assert.That(captured, Is.SameAs(_request));
                _probe.SharedInstances.Enqueue(_shared);
                _probe.FirstEntered.TrySetResult(true);
                if (Interlocked.Increment(ref _probe.Entered) == 2) _probe.BothEntered.TrySetResult(true);
                await _probe.Release.Task;
                Assert.That(_request.DisposeCount, Is.Zero);
                Assert.That(HttpContext.Request.QueryString["name"], Is.EqualTo(name));
                return name;
            }
            public void Dispose() { Assert.That(_request.DisposeCount, Is.Zero); Interlocked.Increment(ref _probe.ControllerDisposeCount); }
        }

        public sealed class Payload { public string Text { get; set; } = ""; }
        public sealed class AsyncController(Probe probe, RequestService request) : WebApiController, IAsyncDisposable
        {
            [Route(HttpVerbs.Get, "/async")] public string Get() => "ok";
            public async ValueTask DisposeAsync()
            {
                await Task.Yield();
                Assert.That(request.DisposeCount, Is.Zero);
                probe.Events.Enqueue("controller");
                if (probe.ThrowOnDispose) throw new Exception("cleanup");
            }
        }
        public sealed class ThrowingConstructorController : WebApiController
        {
            public ThrowingConstructorController(RequestService request) => throw new InvalidOperationException(request.Id.ToString());
            [Route(HttpVerbs.Get, "/value")] public string Get() => "unreachable";
        }
        public interface IMissing { }
        public sealed class MissingDependencyController(IMissing missing) : WebApiController
        {
            [Route(HttpVerbs.Get, "/missing")] public string Get() => missing.ToString()!;
        }
        public sealed class ManualController(Probe probe) : WebApiController
        {
            [Route(HttpVerbs.Get, "/manual")]
            public async Task<string> Get()
            { probe.BothEntered.TrySetResult(true); await probe.Release.Task; probe.Events.Enqueue("action"); return "ok"; }
        }
        public sealed class DualDisposableController(Probe probe) : WebApiController, IDisposable, IAsyncDisposable
        {
            [Route(HttpVerbs.Get, "/dual")] public string Get() => "ok";
            public void Dispose() => probe.Events.Enqueue("sync");
            public ValueTask DisposeAsync() { probe.Events.Enqueue("async"); return ValueTask.CompletedTask; }
        }
        public sealed class LegacyController(Probe probe) : WebApiController, IDisposable
        {
            public bool Disposed;
            [Route(HttpVerbs.Get, "/legacy")]
            public async Task<string> Get()
            {
                probe.FirstEntered.TrySetResult(true);
                await probe.Release.Task;
                return "ok";
            }
            public void Dispose() { Disposed = true; probe.BothEntered.TrySetResult(true); }
        }
        private sealed class TestLifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }

        private sealed class OwnedModule : WebModuleBase, IDisposable
        {
            public OwnedModule() : base("/") { }
            public int DisposeCount;
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context) => Task.CompletedTask;
            public void Dispose() => Interlocked.Increment(ref DisposeCount);
        }
        private sealed class FailingStartModule : WebModuleBase
        {
            public FailingStartModule() : base("/") { }
            public override bool IsFinalHandler => false;
            protected override void OnStart(CancellationToken cancellationToken) => throw new InvalidOperationException("startup");
            protected override Task OnRequestAsync(IHttpContext context) => Task.CompletedTask;
        }
    }
}
