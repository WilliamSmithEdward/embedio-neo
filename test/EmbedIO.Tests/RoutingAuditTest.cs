using System;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RoutingAuditTest
    {
        [TestCase("type")]
        [TestCase("method")]
        [TestCase("delegate")]
        public async Task StaticHandlersCanBeRegisteredAndInvoked(string source)
        {
            var module = new RoutingModule("/");
            var method = typeof(StaticHandlers).GetMethod(nameof(StaticHandlers.Handle))
                ?? throw new InvalidOperationException("Missing fixture handler.");
            object target = source switch
            {
                "type" => typeof(StaticHandlers),
                "method" => method,
                _ => (RouteHandlerCallback)StaticHandlers.Handle,
            };
            Assert.That(module.AddFrom(target), Is.EqualTo(1));
            await TestWebServer.UseAsync(server => server.WithModule(module),
                async client => Assert.That(await client.GetStringAsync("/static"), Is.EqualTo("static")));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LockedCollectionRejectsExistingAndNewMatchers(bool existing)
        {
            var routes = Activator.CreateInstance(typeof(RouteVerbResolverCollection), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { "audit" }, null) as RouteVerbResolverCollection
                ?? throw new InvalidOperationException("Cannot create route collection.");
            routes.Add(HttpVerbs.Get, RouteMatcher.Parse("/existing", false), Handler);
            routes.Lock();
            Assert.Throws<InvalidOperationException>(() => routes.Add(HttpVerbs.Get,
                RouteMatcher.Parse(existing ? "/existing" : "/new", false), Handler));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StartedModuleRejectsExistingAndNewMatchers(bool existing)
        {
            var module = new RoutingModule("/");
            module.Add(HttpVerbs.Get, RouteMatcher.Parse("/existing", false), Handler);
            module.Start(CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => module.Add(HttpVerbs.Get,
                RouteMatcher.Parse(existing ? "/existing" : "/new", false), Handler));
        }

        private static Task Handler(IHttpContext context, RouteMatch route) => Task.CompletedTask;

        public static class StaticHandlers
        {
            [Route(HttpVerbs.Get, "/static")]
            public static Task Handle(IHttpContext context, RouteMatch route)
                => context.SendStringAsync("static", "text/plain", Encoding.UTF8);
        }
    }
}
