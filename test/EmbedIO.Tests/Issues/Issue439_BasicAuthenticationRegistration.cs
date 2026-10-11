using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue439_BasicAuthenticationRegistration
    {
        [TestCase(null, "/api/")]
        [TestCase("", "/api/")]
        [TestCase("Example", "Example")]
        public void ConfigurationRunsBeforeRegistrationAndReturnsConcreteContainer(string? realm, string expectedRealm)
        {
            using var server = new WebServer();
            BasicAuthenticationModule? configured = null;
            WebServer result = server.WithBasicAuthentication("/api", module =>
            {
                Assert.That(server.Modules, Is.Empty);
                configured = module;
                module.WithAccount("user", "secret");
            }, realm);
            Assert.That(result, Is.SameAs(server));
            Assert.That(server.Modules.Single(), Is.SameAs(configured));
            Assert.That((configured ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Realm, Is.EqualTo(expectedRealm));
            Assert.That(configured.Accounts["user"], Is.EqualTo("secret"));
        }

        [Test]
        public void FailedConfigurationDoesNotRegisterAPartialModule()
        {
            using var group = new ModuleGroup("/outer", false);
            var failure = new InvalidOperationException("configuration failed");
            Assert.That(Assert.Throws<InvalidOperationException>(() => group.WithBasicAuthentication("/api",
                m => { m.WithAccount("user", "secret"); throw failure; })), Is.SameAs(failure));
            Assert.That(group.Modules, Is.Empty);
        }

        [Test]
        public void NullConfigurationIsRejectedWithoutRegistration()
        {
            using var server = new WebServer();
            var error = Assert.Throws<ArgumentNullException>(() => server.WithBasicAuthentication("/api", null));
            Assert.That(error.ParamName, Is.EqualTo("configure"));
            Assert.That(server.Modules, Is.Empty);
        }

        [Test]
        public void InvalidRouteRetainsConstructorValidationBeforeConfiguration()
        {
            using var server = new WebServer();
            var invoked = false;
            Assert.Throws<ArgumentException>(() => server.WithBasicAuthentication("relative", _ => invoked = true));
            Assert.That(invoked, Is.False);
            Assert.That(server.Modules, Is.Empty);
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task FluentRegistrationPreservesChallengesCredentialsAndRouteScope(HttpListenerMode mode, bool nested)
        {
            var root = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            using var server = new WebServer(o => o.WithUrlPrefix(root).WithMode(mode));
            var hits = 0;
            if (nested)
            {
                var group = new ModuleGroup("/outer", false);
                ModuleGroup result = group.WithBasicAuthentication("/api", m => m.WithAccount("user", "secret:tail"), "Example");
                Assert.That(result, Is.SameAs(group));
                group.OnGet("/api", c => { Interlocked.Increment(ref hits); return c.SendStringAsync("protected", "text/plain", WebServer.Utf8NoBomEncoding); });
                server.WithModule(group);
            }
            else
            {
                server.WithBasicAuthentication("/api", m => m.WithAccount("user", "secret:tail"), "Example")
                    .OnGet("/api", c => { Interlocked.Increment(ref hits); return c.SendStringAsync("protected", "text/plain", WebServer.Utf8NoBomEncoding); });
            }
            server.OnGet("/public", c => c.SendStringAsync("public", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            var path = root + (nested ? "outer/" : "") + "api/child";
            try
            {
                foreach (var authorization in new string?[] { null, "Basic !!!", "Bearer unrelated", "Basic " + Encode("user:wrong") })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, path);
                    if (authorization != null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("Basic realm=\"Example\" charset=UTF-8"));
                    Assert.That(hits, Is.Zero);
                }
                using var valid = new HttpRequestMessage(HttpMethod.Get, path);
                valid.Headers.TryAddWithoutValidation("Authorization", "bAsIc " + Encode("user:secret:tail"));
                using var accepted = await client.SendAsync(valid);
                Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await accepted.Content.ReadAsStringAsync(), Is.EqualTo("protected"));
                Assert.That(hits, Is.EqualTo(1));
                using var outside = await client.GetAsync(root + "public");
                Assert.That(await outside.Content.ReadAsStringAsync(), Is.EqualTo("public"));
                Assert.That(outside.Headers.WwwAuthenticate, Is.Empty);
                using var deniedAgain = await client.GetAsync(path);
                Assert.That(deniedAgain.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(hits, Is.EqualTo(1));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static string Encode(string credentials) => Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
    }
}
