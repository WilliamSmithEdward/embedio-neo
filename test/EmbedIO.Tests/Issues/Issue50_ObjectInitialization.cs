using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Authentication;
using EmbedIO.Files;
using EmbedIO.Sessions;
using EmbedIO.Tests.TestObjects;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #50 evaluates object initialization as a companion to fluent setup. The
    // fluent helpers are thin wrappers over public setters and Modules.Add, and
    // C# object and collection initializers already reach both. These cases build
    // the same server both ways and require identical behavior over real HTTP.
    public class Issue50_ObjectInitialization
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [Test]
        public async Task FluentAndInitializerServersRespondIdentically()
        {
            using var root = new TemporaryFolder();
            var fluentUrl = Resources.GetServerAddress();
            var initUrl = Resources.GetServerAddress();

            using var fluent = new WebServer(o => o.WithUrlPrefix(fluentUrl).WithMode(HttpListenerMode.EmbedIO))
                .WithLocalSessionManager(sessions => sessions.CookieName = "app")
                .WithModule("api", new ActionModule("/api", HttpVerbs.Get, Api), module => module.OnUnhandledException = ExceptionHandler.EmptyResponse)
                .WithModule("files", new FileModule("/", new FileSystemProvider(root.Path, true)).WithDefaultDocument("index.html").WithoutContentCaching());
            fluent.OnHttpException = HttpExceptionHandler.PlainTextResponse;

            var options = new WebServerOptions { Mode = HttpListenerMode.EmbedIO };
            options.AddUrlPrefix(initUrl);
            using var initialized = new WebServer(options)
            {
                SessionManager = new LocalSessionManager { CookieName = "app" },
                OnHttpException = HttpExceptionHandler.PlainTextResponse,
                Modules =
                {
                    { "api", new ActionModule("/api", HttpVerbs.Get, Api) { OnUnhandledException = ExceptionHandler.EmptyResponse } },
                    { "files", new FileModule("/", new FileSystemProvider(root.Path, true)) { DefaultDocument = "index.html", ContentCaching = false } },
                },
            };

            Assert.That(initialized.Modules.WithSafeNames.Select(entry => entry.SafeName), Is.EqualTo(fluent.Modules.WithSafeNames.Select(entry => entry.SafeName)));
            await using var fluentHost = await Host.StartAsync(fluent);
            await using var initHost = await Host.StartAsync(initialized);
            foreach (var path in new[] { "api", "api/fail", "", "index.html", "missing.txt" })
            {
                var expected = await Describe(fluentUrl + path);
                var actual = await Describe(initUrl + path);
                Assert.That(actual, Is.EqualTo(expected), "Path /" + path);
            }
            Assert.That(await Describe(fluentUrl), Does.StartWith("200|text/html|<h1>home</h1>"));
            Assert.That(await Describe(fluentUrl + "api"), Does.Contain("Set-Cookie:app="), "The session manager is configured the same way on both servers.");

            static Task Api(IHttpContext context)
            {
                if (context.RequestedPath == "/fail") throw new InvalidOperationException("api failure");
                _ = context.Session["visits"] = 1;
                return context.SendStringAsync("api", "text/plain", WebServer.Utf8NoBomEncoding);
            }
        }

        [Test]
        public async Task CollectionInitializerPreservesRegistrationOrder()
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(new WebServerOptions { Mode = HttpListenerMode.EmbedIO }.WithUrlPrefix(url))
            {
                Modules =
                {
                    new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("first", "text/plain", WebServer.Utf8NoBomEncoding)),
                    { "second", new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("second", "text/plain", WebServer.Utf8NoBomEncoding)) },
                },
            };
            Assert.That(server.Modules.Count, Is.EqualTo(2));
            Assert.That(server.Modules.Named.Keys, Is.EqualTo(new[] { "second" }));
            await using var host = await Host.StartAsync(server);
            using var client = new HttpClient { Timeout = Deadline };
            Assert.That(await client.GetStringAsync(url), Is.EqualTo("first"), "The first module in the initializer handles the request, as with WithModule.");
        }

        [Test]
        public void CollectionInitializerRejectsDuplicatesLikeWithModule()
        {
            var module = new ActionModule("/", HttpVerbs.Get, _ => Task.CompletedTask);
            Assert.That(() => new WebServer(new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress())) { Modules = { module, module } },
                Throws.ArgumentException.With.Property("ParamName").EqualTo("component"));
            Assert.That(() => new WebServer(new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress())).WithModule(module).WithModule(module),
                Throws.ArgumentException.With.Property("ParamName").EqualTo("component"));
            Assert.That(() => new WebServer(new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress())) { Modules = { { "a", new ActionModule("/", HttpVerbs.Get, _ => Task.CompletedTask) }, { "a", new ActionModule("/x", HttpVerbs.Get, _ => Task.CompletedTask) } } },
                Throws.ArgumentException.With.Property("ParamName").EqualTo("name"));
        }

        [TestCase("html")]
        [TestCase("x")]
        public void InvalidValuesFailIdenticallyThroughSetterAndFluentHelper(string extension)
        {
            using var root = new TemporaryFolder();
            var viaSetter = Assert.Throws<ArgumentException>(() => _ = new FileModule("/", new FileSystemProvider(root.Path, true)) { DefaultExtension = extension });
            var viaFluent = Assert.Throws<ArgumentException>(() => new FileModule("/", new FileSystemProvider(root.Path, true)).WithDefaultExtension(extension));
            Assert.That(viaFluent?.Message, Is.EqualTo(viaSetter?.Message));
            Assert.That(viaFluent?.ParamName, Is.EqualTo(viaSetter?.ParamName));
            Assert.That(viaSetter?.StackTrace, Does.Contain("set_DefaultExtension"), "The setter frame appears in both traces.");
            Assert.That(viaFluent?.StackTrace, Does.Contain("set_DefaultExtension").And.Contain("WithDefaultExtension"));
        }

        [Test]
        public void SessionValidationIsIdenticalThroughInitializerAndConfigureCallback()
        {
            var viaInitializer = Assert.Throws<ArgumentException>(() => _ = new LocalSessionManager { CookieName = "bad name" });
            var viaFluent = Assert.Throws<ArgumentException>(() => new WebServer(new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress()))
                .WithLocalSessionManager(sessions => sessions.CookieName = "bad name"));
            Assert.That(viaFluent?.Message, Is.EqualTo(viaInitializer?.Message));
        }

        [Test]
        public void DefaultsAreIdenticalWithoutAnySetup()
        {
            using var root = new TemporaryFolder();
            var plain = new FileModule("/", new FileSystemProvider(root.Path, true));
            var cleared = new FileModule("/", new FileSystemProvider(root.Path, true)) { DefaultDocument = "x.html", DefaultExtension = ".x" }
                .WithoutDefaultDocument().WithoutDefaultExtension();
            Assert.That(cleared.DefaultDocument, Is.EqualTo(plain.DefaultDocument).Or.Null);
            Assert.That(cleared.DefaultExtension, Is.EqualTo(plain.DefaultExtension));
            Assert.That(new WebServerOptions().Mode, Is.EqualTo(new WebServerOptions().WithMode(new WebServerOptions().Mode).Mode));
            var initializerSessions = new LocalSessionManager { };
            var fluentSessions = new WebServer(new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress())).WithLocalSessionManager().SessionManager as LocalSessionManager;
            Assert.That(fluentSessions?.CookieName, Is.EqualTo(initializerSessions.CookieName));
            Assert.That(fluentSessions?.SessionDuration, Is.EqualTo(initializerSessions.SessionDuration));
        }

        [Test]
        public async Task AccountIndexInitializerAuthenticatesLikeWithAccount()
        {
            var fluentUrl = Resources.GetServerAddress();
            var initUrl = Resources.GetServerAddress();
            using var fluent = new WebServer(o => o.WithUrlPrefix(fluentUrl).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new BasicAuthenticationModule("/", "realm").WithAccount("ana", "secret"))
                .WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("granted", "text/plain", WebServer.Utf8NoBomEncoding));
            using var initialized = new WebServer(new WebServerOptions { Mode = HttpListenerMode.EmbedIO }.WithUrlPrefix(initUrl))
            {
                Modules =
                {
                    new BasicAuthenticationModule("/", "realm") { Accounts = { ["ana"] = "secret" } },
                    new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("granted", "text/plain", WebServer.Utf8NoBomEncoding)),
                },
            };
            await using var fluentHost = await Host.StartAsync(fluent);
            await using var initHost = await Host.StartAsync(initialized);
            foreach (var credentials in new[] { null, "ana:secret", "ana:wrong" })
                Assert.That(await DescribeAuthenticated(initUrl, credentials), Is.EqualTo(await DescribeAuthenticated(fluentUrl, credentials)), credentials ?? "<none>");
            Assert.That(await DescribeAuthenticated(initUrl, "ana:secret"), Is.EqualTo("200|granted"));
        }

        private static async Task<string> Describe(string url)
        {
            using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = Deadline };
            using var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            var cookie = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join(";", values.Select(v => v.Split('=')[0] + "=")) : "";
            return $"{(int)response.StatusCode}|{response.Content.Headers.ContentType?.MediaType}|{body}|Set-Cookie:{cookie}";
        }

        private static async Task<string> DescribeAuthenticated(string url, string? credentials)
        {
            using var client = new HttpClient { Timeout = Deadline };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (credentials != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
            using var response = await client.SendAsync(request);
            return $"{(int)response.StatusCode}|{await response.Content.ReadAsStringAsync()}";
        }

        private sealed class TemporaryFolder : IDisposable
        {
            public TemporaryFolder()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embedio-50-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
                File.WriteAllText(System.IO.Path.Combine(Path, "index.html"), "<h1>home</h1>");
            }

            public string Path { get; }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); }
                catch (IOException) { }
            }
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;
            private Host(WebServer server) { _running = server.RunAsync(_stop.Token); }
            internal static Task<Host> StartAsync(WebServer server) => Task.FromResult(new Host(server));

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(Deadline); }
                finally { _stop.Dispose(); }
            }
        }
    }
}
