using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authorization;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed class BeforeHandlerTest
    {
        [TestCase(false, "default", "controller,class,action")]
        [TestCase(true, "default", "controller,class,action")]
        [TestCase(false, "replace", "method-start,method-end,action")]
        [TestCase(true, "replace", "method-start,method-end,action")]
        [TestCase(false, "append", "controller,method-start,method-end,action")]
        [TestCase(true, "append", "controller,method-start,method-end,action")]
        [TestCase(false, "skip", "action")]
        [TestCase(true, "skip", "action")]
        [TestCase(false, "inherited", "base,action")]
        [TestCase(true, "inherited", "base,action")]
        public async Task MethodSelectionAndInheritedDefaultsMatch(bool generated, string path, string expected)
        {
            var controller = new HookController();
            var module = Module(generated, () => controller);
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var response = await client.GetAsync("/api/" + path);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(expected));
                Assert.That(controller.Log, Is.EqualTo((expected + ",dispose").Split(',')));
                Assert.That(response.Headers.CacheControl?.NoStore == true, Is.EqualTo(expected.StartsWith("controller", StringComparison.Ordinal)));
            });
        }

        [TestCase(false, "reject", HttpStatusCode.Forbidden, "reject")]
        [TestCase(true, "reject", HttpStatusCode.Forbidden, "reject")]
        [TestCase(false, "fault", HttpStatusCode.InternalServerError, "fault")]
        [TestCase(true, "fault", HttpStatusCode.InternalServerError, "fault")]
        [TestCase(false, "null", HttpStatusCode.InternalServerError, "null")]
        [TestCase(true, "null", HttpStatusCode.InternalServerError, "null")]
        public async Task HookFailuresPrecedeBodyBindingAndReleaseTheController(bool generated, string path, HttpStatusCode status, string entry)
        {
            var controller = new HookController();
            await TestWebServer.UseAsync(server => server.WithModule(Module(generated, () => controller)), async client =>
            {
                using var content = new StringContent("body");
                using var response = await client.PostAsync("/api/" + path, content);
                Assert.That(response.StatusCode, Is.EqualTo(status));
                Assert.That(controller.Log, Is.EqualTo(new[] { entry, "dispose" }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SkipCannotBypassAuthorizationOrActivateTheController(bool generated)
        {
            var created = 0;
            await TestWebServer.UseAsync(server => server.WithModule(Module(generated, () => { created++; return new HookController(); })), async client =>
            {
                using var response = await client.GetAsync("/api/protected");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(created, Is.Zero);
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task AsyncHooksPreserveOwnedAndRequestAwareReleaseTiming(bool generated, bool requestAware)
        {
            var controller = new HookLifetimeController();
            var module = new WebApiModule("/api");
            if (requestAware)
            {
                if (generated) module.RegisterControllerWithContext(HookBindings.HookLifetimeController, _ => controller, (_, c) => { c.Dispose(); return Task.CompletedTask; });
                else module.RegisterControllerWithContext(typeof(HookLifetimeController), _ => controller, (_, c) => { ((HookLifetimeController)c).Dispose(); return Task.CompletedTask; });
            }
            else if (generated) module.RegisterController(HookBindings.HookLifetimeController, () => controller);
            else module.RegisterController(() => controller);
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                var request = client.GetAsync("/api/value");
                try
                {
                    await controller.HookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(controller.ActionEntered.Task.IsCompleted, Is.False);
                    Assert.That(controller.Disposed.Task.IsCompleted, Is.False);
                    controller.HookRelease.TrySetResult();
                    await controller.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (requestAware) Assert.That(controller.Disposed.Task.IsCompleted, Is.False);
                    else await controller.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { controller.HookRelease.TrySetResult(); controller.ActionRelease.TrySetResult(); }
                using var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                await controller.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(controller.DisposeCount, Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SuccessfulHookRunsBeforeBodyBinding(bool generated)
        {
            var controller = new HookController();
            await TestWebServer.UseAsync(server => server.WithModule(Module(generated, () => controller)), async client =>
            {
                using var content = new StringContent("body");
                using var response = await client.PostAsync("/api/body", content);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("method-start,method-end,body,action"));
                Assert.That(controller.Log, Is.EqualTo(new[] { "method-start", "method-end", "body", "action", "dispose" }));
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task CancellationDuringTheHookReleasesWithoutInvokingTheAction(bool generated, bool requestAware)
        {
            var controller = new HookLifetimeController();
            var module = new WebApiModule("/api");
            if (requestAware)
            {
                if (generated) module.RegisterControllerWithContext(HookBindings.HookLifetimeController, _ => controller, (_, c) => { c.Dispose(); return Task.CompletedTask; });
                else module.RegisterControllerWithContext(typeof(HookLifetimeController), _ => controller, (_, c) => { ((HookLifetimeController)c).Dispose(); return Task.CompletedTask; });
            }
            else if (generated) module.RegisterController(HookBindings.HookLifetimeController, () => controller);
            else module.RegisterController(() => controller);
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var request = client.GetAsync("/api/value", cancel.Token);
                await controller.HookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancel.Cancel();
                await Assert.ThatAsync(async () => { using var response = await request; }, Throws.InstanceOf<OperationCanceledException>());
                await controller.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(controller.ActionEntered.Task.IsCompleted, Is.False);
                Assert.That(controller.DisposeCount, Is.EqualTo(1));
            });
        }

        [TestCase(typeof(MissingHookController))]
        [TestCase(typeof(InvalidHookController))]
        [TestCase(typeof(AmbiguousHookController))]
        [TestCase(typeof(InvalidSkipHookController))]
        public void InvalidHooksFailRegistrationBeforeAddingControllers(Type type)
        {
            var module = new WebApiModule("/api");
            Assert.That(() => module.RegisterController(type), Throws.ArgumentException);
            Assert.That(module.ControllerCount, Is.Zero);
        }

        private static WebApiModule Module(bool generated, Func<HookController> factory)
        {
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated) module.RegisterController(HookBindings.HookController, factory);
            else module.RegisterController(factory);
            return module;
        }
    }

    [GeneratedControllerBinding(typeof(HookController))]
    [GeneratedControllerBinding(typeof(HookLifetimeController))]
    internal static partial class HookBindings { }

    [BeforeHandler(nameof(ClassHook))]
    public abstract class HookBaseController : WebApiController
    {
        public List<string> Log { get; } = new();
        protected override void OnBeforeHandler() { Log.Add("controller"); base.OnBeforeHandler(); }
        public void ClassHook() => Log.Add("class");
        public void BaseHook() => Log.Add("base");
        [Route(HttpVerbs.Get, "/inherited")]
        [BeforeHandler(nameof(BaseHook), Behavior = BeforeHandlerBehavior.Replace)]
        public virtual string Inherited() => Result();
        protected string Result() { Log.Add("action"); return string.Join(",", Log); }
    }

    public sealed class HookController : HookBaseController, IDisposable
    {
        [Route(HttpVerbs.Get, "/default")]
        public string Default() => Result();
        [Route(HttpVerbs.Get, "/replace")]
        [BeforeHandler(nameof(MethodHook), Behavior = BeforeHandlerBehavior.Replace)]
        public string Replace() => Result();
        [Route(HttpVerbs.Get, "/append")]
        [BeforeHandler(nameof(MethodHook))]
        public string Append() => Result();
        [Route(HttpVerbs.Get, "/skip")]
        [BeforeHandler(Behavior = BeforeHandlerBehavior.Skip)]
        public string Skip() => Result();
        [Route(HttpVerbs.Post, "/body")]
        [BeforeHandler(nameof(MethodHook), Behavior = BeforeHandlerBehavior.Replace)]
        public string Body([HookBody] string body) => Result();
        public override string Inherited() => Result();
        public async Task MethodHook(CancellationToken token) { Log.Add("method-start"); await Task.Yield(); token.ThrowIfCancellationRequested(); Log.Add("method-end"); }
        public void Reject() { Log.Add("reject"); throw HttpException.Forbidden(); }
        public async Task Fault() { Log.Add("fault"); await Task.Yield(); throw new InvalidOperationException("hook failed"); }
        public Task? Null() { Log.Add("null"); return null; }
        [Route(HttpVerbs.Post, "/reject")]
        [BeforeHandler(nameof(Reject), Behavior = BeforeHandlerBehavior.Replace)]
        public string Rejected([HookBody] string body) => body;
        [Route(HttpVerbs.Post, "/fault")]
        [BeforeHandler(nameof(Fault), Behavior = BeforeHandlerBehavior.Replace)]
        public string Faulted([HookBody] string body) => body;
        [Route(HttpVerbs.Post, "/null")]
        [BeforeHandler(nameof(Null), Behavior = BeforeHandlerBehavior.Replace)]
        public string NullTask([HookBody] string body) => body;
        [Authorize]
        [Route(HttpVerbs.Get, "/protected")]
        [BeforeHandler(Behavior = BeforeHandlerBehavior.Skip)]
        public string Protected() => Result();
        public void Dispose() => Log.Add("dispose");
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class HookBodyAttribute : Attribute, IRequestDataAttribute<HookController, string>
    {
        public Task<string?> GetRequestDataAsync(HookController controller, string name)
        { ArgumentNullException.ThrowIfNull(controller); controller.Log.Add("body"); return Task.FromResult<string?>("body"); }
    }

    public sealed class HookLifetimeController : WebApiController, IDisposable
    {
        public TaskCompletionSource HookEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HookRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ActionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ActionRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public async Task Prepare(CancellationToken token) { HookEntered.TrySetResult(); await HookRelease.Task.WaitAsync(token); }
        [Route(HttpVerbs.Get, "/value")]
        [BeforeHandler(nameof(Prepare), Behavior = BeforeHandlerBehavior.Replace)]
        public async Task Action() { ActionEntered.TrySetResult(); await ActionRelease.Task; }
        public void Dispose() { DisposeCount++; Disposed.TrySetResult(); }
    }

    public sealed class MissingHookController : WebApiController
    {
        [Route(HttpVerbs.Get, "/value"), BeforeHandler("Missing")]
        public void Get() { }
    }
    public sealed class InvalidHookController : WebApiController
    {
        public int Prepare() => 1;
        [Route(HttpVerbs.Get, "/value"), BeforeHandler(nameof(Prepare))]
        public void Get() { }
    }
    public sealed class AmbiguousHookController : WebApiController
    {
        public void Prepare() { }
        public void Prepare(CancellationToken token) => token.ThrowIfCancellationRequested();
        [Route(HttpVerbs.Get, "/value"), BeforeHandler(nameof(Prepare))]
        public void Get() { }
    }
    public sealed class InvalidSkipHookController : WebApiController
    {
        public void Prepare() { }
        [Route(HttpVerbs.Get, "/value"), BeforeHandler(nameof(Prepare), Behavior = BeforeHandlerBehavior.Skip)]
        public void Get() { }
    }
}
