using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Files;
using EmbedIO.Sessions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #49 evaluates init-only configuration against the existing locked model.
    // These cases pin when each configuration surface locks, what object
    // initialization already allows, where validation runs and which nested objects
    // stay mutable, so any alternative can be compared against measured behavior.
    public class Issue49_ConfigurationLocking
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [Test]
        public void ObjectInitializationAndFluentSetupProduceEquivalentOptions()
        {
            var url = Resources.GetServerAddress();
            var initialized = new WebServerOptions { Mode = HttpListenerMode.EmbedIO, AutoLoadCertificate = false };
            initialized.AddUrlPrefix(url);
            var fluent = new WebServerOptions().WithMode(HttpListenerMode.EmbedIO).WithoutAutoLoadCertificate().WithUrlPrefix(url);
            Assert.That(initialized.Mode, Is.EqualTo(fluent.Mode));
            Assert.That(initialized.AutoLoadCertificate, Is.EqualTo(fluent.AutoLoadCertificate));
            Assert.That(initialized.UrlPrefixes, Is.EqualTo(fluent.UrlPrefixes));
            Assert.That(initialized.ConfigurationLocked, Is.False);
            Assert.That(fluent.ConfigurationLocked, Is.False);
        }

        [Test]
        public void OptionsLockWhenTheServerIsConstructedNotWhenItStarts()
        {
            var options = new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress()).WithMode(HttpListenerMode.EmbedIO);
            using var server = new WebServer(options);
            Assert.That(options.ConfigurationLocked, Is.True, "The listener is created from the options in the constructor.");
            Assert.That(server.State, Is.EqualTo(WebServerState.Created));
            Assert.That(() => options.Mode = HttpListenerMode.EmbedIO, Throws.InvalidOperationException);
            Assert.That(() => options.AddUrlPrefix(Resources.GetServerAddress()), Throws.InvalidOperationException);
            Assert.That(() => options.SupportCompressedRequests = true, Throws.InvalidOperationException);
            Assert.That(options.UrlPrefixes, Has.Count.EqualTo(1), "A rejected mutation leaves the collection unchanged.");
        }

        [Test]
        public void UrlPrefixViewCannotBeCastBackToBypassTheLock()
        {
            var options = new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress());
            options.Lock();
            Assert.That(options.UrlPrefixes, Is.Not.InstanceOf<List<string>>(), "The view is not the backing list.");
            var asList = options.UrlPrefixes as IList<string>;
            if (asList != null)
            {
                Assert.That(asList.IsReadOnly, Is.True);
                Assert.That(() => asList.Add(Resources.GetServerAddress()), Throws.InstanceOf<NotSupportedException>());
            }
            Assert.That(options.UrlPrefixes, Has.Count.EqualTo(1));
            Assert.That(ReferenceEquals(options.UrlPrefixes, options.UrlPrefixes), Is.True, "The view is created once, not per access.");
        }

        [Test]
        public void UrlPrefixViewTracksAdditionsMadeBeforeLocking()
        {
            var options = new WebServerOptions();
            var view = options.UrlPrefixes;
            options.AddUrlPrefix("http://localhost:1/");
            options.AddUrlPrefix("http://localhost:2/");
            Assert.That(view, Is.EqualTo(new[] { "http://localhost:1/", "http://localhost:2/" }), "A view taken early still reflects later additions.");
            Assert.That(() => options.AddUrlPrefix("http://localhost:1/"), Throws.ArgumentException, "Duplicate prefixes are rejected at the call.");
        }

        [Test]
        public async Task ModuleCollectionViewsCannotBeCastBackToInjectAModule()
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule("hello", new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                var intruder = new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("intruder", "text/plain", WebServer.Utf8NoBomEncoding));
                Assert.That(server.Modules.WithSafeNames, Is.Not.InstanceOf<List<(string, IWebModule)>>());
                if (server.Modules.WithSafeNames is IList<(string, IWebModule)> list)
                    Assert.That(() => list.Insert(0, ("intruder", intruder)), Throws.InstanceOf<NotSupportedException>());
                Assert.That(server.Modules.Named, Is.Not.InstanceOf<Dictionary<string, IWebModule>>());
                if (server.Modules.Named is IDictionary<string, IWebModule> named)
                    Assert.That(() => named.Add("intruder", intruder), Throws.InstanceOf<NotSupportedException>());
                Assert.That(server.Modules.Count, Is.EqualTo(1));
                Assert.That(server.Modules.Named.Keys, Is.EqualTo(new[] { "hello" }));
                using var client = new HttpClient { Timeout = Deadline };
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("hello"), "Dispatch still uses only the registered module.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(Deadline);
            }
        }

        [Test]
        public void LockedOptionsCanConfigureAReplacementServer()
        {
            var options = new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress()).WithMode(HttpListenerMode.EmbedIO);
            using (new WebServer(options)) { }
            Assert.That(options.ConfigurationLocked, Is.True);
            Assert.That(() => new WebServer(options).Dispose(), Throws.Nothing,
                "Locking is idempotent, so the same frozen options configure the next instance after a stopped server.");
        }

        [Test]
        public async Task ServerAndModulesStayMutableUntilStartThenLock()
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            var module = new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding));
            server.OnUnhandledException = ExceptionHandler.EmptyResponse;
            module.OnUnhandledException = ExceptionHandler.EmptyResponse;
            server.WithModule(module);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.ConfigurationLocked, Is.True);
                Assert.That(module.ConfigurationLocked, Is.True);
                Assert.That(() => server.OnUnhandledException = ExceptionHandler.HtmlResponse, Throws.InvalidOperationException);
                Assert.That(() => module.OnUnhandledException = ExceptionHandler.HtmlResponse, Throws.InvalidOperationException);
                Assert.That(() => server.WithModule(new ActionModule("/late", HttpVerbs.Get, _ => Task.CompletedTask)), Throws.InvalidOperationException);
                Assert.That(() => server.SessionManager = new LocalSessionManager(), Throws.InvalidOperationException);
                using var client = new HttpClient { Timeout = Deadline };
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("ok"), "Rejected mutations leave the running configuration intact.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(Deadline);
            }
        }

        [Test]
        public async Task FileModuleSettingsLockAtStartButItsCacheStaysTunable()
        {
            var url = Resources.GetServerAddress();
            using var cache = new FileCache { MaxSizeKb = 1024 };
            var files = new FileModule("/", new ZeroLengthProvider()) { Cache = cache, DefaultDocument = "index.html", ContentCaching = true };
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)).WithModule(files);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(() => files.DefaultDocument = "default.html", Throws.InvalidOperationException);
                Assert.That(() => files.Cache = new FileCache(), Throws.InvalidOperationException, "Replacing the cache is locked.");
                cache.MaxSizeKb = 2048;
                Assert.That(cache.MaxSizeKb, Is.EqualTo(2048), "The borrowed cache is a nested object with its own runtime limits; locking the module does not freeze it.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(Deadline);
            }
        }

        [Test]
        public async Task InvalidSessionConfigurationFailsAtStartupNotAtAssignment()
        {
            var url = Resources.GetServerAddress();
            var sessions = new LocalSessionManager { CookieSameSite = CookieSameSiteMode.None, CookieSecure = false };
            Assert.That(sessions.CookieSameSite, Is.EqualTo(CookieSameSiteMode.None), "Assignment accepts an incomplete combination; order of setters does not matter.");
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            server.SessionManager = sessions;
            Assert.That(async () => await server.RunAsync().WaitAsync(Deadline), Throws.InvalidOperationException.With.Message.Contains("CookieSecure"));
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
            using var replacement = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("bound", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource();
            var running = replacement.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = Deadline };
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("bound"), "The failed startup released its port.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(Deadline);
            }
        }

        [Test]
        public void SessionSettingsLockOnlyWhenTheManagerStarts()
        {
            var sessions = new LocalSessionManager { CookieName = "app", CookieSecure = true, CookieSameSite = CookieSameSiteMode.None };
            sessions.CookieName = "renamed";
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            sessions.Start(stop.Token);
            Assert.That(() => sessions.CookieName = "late", Throws.InvalidOperationException);
            Assert.That(sessions.CookieName, Is.EqualTo("renamed"));
        }

        [Test]
        public void ExplicitLockIsIdempotent()
        {
            var options = new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress());
            options.Lock();
            Assert.That(options.Lock, Throws.Nothing);
            Assert.That(options.ConfigurationLocked, Is.True);
        }

        private sealed class ZeroLengthProvider : IFileProvider
        {
            public event Action<string>? ResourceChanged { add { } remove { } }
            public bool IsImmutable => true;
            public void Start(CancellationToken cancellationToken) { }
            public MappedResourceInfo? MapUrlPath(string requestPath, IMimeTypeProvider mimeTypeProvider) => null;
            public System.IO.Stream? OpenFile(string path) => null;
            public System.Collections.Generic.IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mimeTypeProvider)
                => Array.Empty<MappedResourceInfo>();
        }
    }
}
