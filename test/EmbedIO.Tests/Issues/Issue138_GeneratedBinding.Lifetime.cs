using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Controller lifetime and registration rules of generated bindings, compared with
    // reflection-based registration where both accept the controller.
    [TestFixture]
    public sealed class Issue138_GeneratedBindingLifetime
    {
        // Reflection disposes a disposable controller as soon as the method returns its task,
        // before the task completes; the generated binding keeps that timing.
        [TestCase(false)]
        [TestCase(true)]
        public async Task DisposalTimingMatchesReflection(bool generated)
        {
            var log = new ConcurrentQueue<string>();
            var module = new WebApiModule("/");
            if (generated)
                module.WithController(Issue138Bindings.Issue138DisposableController, () => new Issue138DisposableController(log));
            else
                module.WithController(() => new Issue138DisposableController(log));

            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient();
            Assert.That(await client.GetStringAsync(server.Url + "dispose/50"), Is.EqualTo("\"worked\""));
            Assert.That(log, Is.EqualTo(new[] { "handler-start", "dispose", "handler-end" }));

            log.Clear();
            using var thrown = await client.GetAsync(server.Url + "dispose-throw");
            Assert.That(thrown.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(log, Is.EqualTo(new[] { "handler-start", "dispose" }));
        }

        // Context-aware registration awaits release after the handler and serializer finish.
        [Test]
        public async Task ContextRegistrationReleasesAfterCompletion()
        {
            var log = new ConcurrentQueue<string>();
            var module = new WebApiModule("/");
            module.RegisterControllerWithContext(
                Issue138Bindings.Issue138DisposableController,
                context => context.Request.QueryString["none"] == null ? new Issue138DisposableController(log) : null,
                (_, _) =>
                {
                    log.Enqueue("release");
                    return Task.CompletedTask;
                });

            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient();
            Assert.That(await client.GetStringAsync(server.Url + "dispose/30"), Is.EqualTo("\"worked\""));

            // Release follows the response write, so the client can finish reading first.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (log.Count < 3 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.That(log, Is.EqualTo(new[] { "handler-start", "handler-end", "release" }));

            log.Clear();
            using var missing = await client.GetAsync(server.Url + "dispose/1?none=1");
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(log, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NullFactoryResultFailsTheRequestLikeReflection(bool generated)
        {
            var module = new WebApiModule("/");
            if (generated)
                module.WithController(Issue138Bindings.Issue138DisposableController, Missing<Issue138DisposableController>);
            else
                module.WithController(Missing<Issue138DisposableController>);

            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient();
            using var response = await client.GetAsync(server.Url + "dispose/1");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        }

        [Test]
        public void RegistrationRulesMatchReflection()
        {
            var module = new WebApiModule("/");
            module.WithController(Issue138Bindings.Issue138ParityController);
            Assert.That(module.ControllerCount, Is.EqualTo(1));
            Assert.Throws<ArgumentException>(() => module.WithController(Issue138Bindings.Issue138ParityController));
            Assert.Throws<ArgumentException>(() => module.WithController<Issue138ParityController>());
            Assert.Throws<ArgumentNullException>(() => module.WithController(Missing<WebApiControllerBinding<Issue138AliasController>>()));
            Assert.Throws<ArgumentNullException>(() => module.WithController(Issue138Bindings.Issue138AliasController, Missing<Func<Issue138AliasController>>()));
            Assert.That(module.ControllerCount, Is.EqualTo(1));
        }

        [Test]
        public void EmptyOrInvalidBindingsLeaveTheModuleUnchanged()
        {
            var module = new WebApiModule("/");
            Assert.Throws<ArgumentException>(() => module.WithController(new WebApiControllerBinding<Issue138AliasController>()));

            static Task<Task> Invoke(Issue138AliasController controller, IHttpContext context, RouteMatch route, ResponseSerializerCallback serializer)
                => Task.FromResult(Task.CompletedTask);
            var invalid = new WebApiControllerBinding<Issue138AliasController>(
                new WebApiRouteBinding<Issue138AliasController>(HttpVerbs.Get, "/valid", false, "valid", () => Invoke),
                new WebApiRouteBinding<Issue138AliasController>(HttpVerbs.Get, "no-leading-slash", false, "invalid", () => Invoke));
            Assert.That(() => module.WithController(invalid), Throws.InstanceOf<FormatException>().Or.InstanceOf<ArgumentException>());
            Assert.That(module.ControllerCount, Is.Zero);

            module.WithController(Issue138Bindings.Issue138AliasController);
            Assert.That(module.ControllerCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ConfigurationIsLockedOnceTheServerStarts()
        {
            var module = new WebApiModule("/").WithController(Issue138Bindings.Issue138AliasController);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient();
            Assert.That(await client.GetStringAsync(server.Url + "Alias/1"), Is.EqualTo("\"alias 1\""));
            Assert.Throws<InvalidOperationException>(() => module.WithController(Issue138Bindings.Issue138OtherAliasController));
        }

        // Case-only aliases of one method share a handler; a different method on a
        // case-equivalent route is rejected, exactly as with reflection registration.
        [TestCase(false)]
        [TestCase(true)]
        public async Task CaseInsensitiveRoutesFollowTheReflectionRules(bool generated)
        {
            var module = new WebApiModule("/") { CaseInsensitiveRoutes = true };
            if (generated)
                module.WithController(Issue138Bindings.Issue138AliasController);
            else
                module.WithController<Issue138AliasController>();

            Assert.Throws<ArgumentException>(() =>
            {
                if (generated)
                    module.WithController(Issue138Bindings.Issue138OtherAliasController);
                else
                    module.WithController<Issue138OtherAliasController>();
            });
            Assert.That(module.ControllerCount, Is.EqualTo(1));

            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient();
            Assert.That(await client.GetStringAsync(server.Url + "ALIAS/x"), Is.EqualTo("\"alias x\""));
            Assert.That(await client.GetStringAsync(server.Url + "alias/y"), Is.EqualTo("\"alias y\""));
            using var posted = await client.PostAsync(server.Url + "aLiAs/z", new StringContent(string.Empty));
            Assert.That(await posted.Content.ReadAsStringAsync(), Is.EqualTo("\"post z\""));
        }

        // Generated bindings pass typed null defaults that reflection registration cannot express.
        [Test]
        public async Task NullDefaultsWorkOnlyWithGeneratedBindings()
        {
            Assert.Throws<ArgumentException>(() => new WebApiModule("/").WithController<Issue138NullDefaultsController>());

            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new WebApiModule("/").WithController(Issue138Bindings.Issue138NullDefaultsController));
            using var client = new HttpClient();
            Assert.That(await client.GetStringAsync(server.Url + "null-defaults"), Is.EqualTo("\"null|null|0|null\""));
        }

        // A null of a non-nullable reference type, for testing argument and factory null checks:
        // array elements start as null without a nullable conversion.
        private static T Missing<T>()
            where T : class
            => (new T[1])[0];

        [Test]
        public void BindingsListEveryRouteAttribute()
        {
            var routes = Issue138Bindings.Issue138ParityController.Routes;
            Assert.That(routes.Count(r => r.Route == "/inherited" && r.Verb == HttpVerbs.Get), Is.EqualTo(1));
            Assert.That(routes.Single(r => r.Route == "/base/").IsBaseRoute, Is.True);
            Assert.That(routes.Where(r => r.Route is "/any" or "/any-alias").Select(r => r.HandlerId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(routes.Where(r => r.Route.StartsWith("/over/", StringComparison.Ordinal)).Select(r => r.HandlerId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(Issue138Bindings.Nested.Issue138DisposableController.Routes, Has.Count.EqualTo(2));
        }
    }
}
