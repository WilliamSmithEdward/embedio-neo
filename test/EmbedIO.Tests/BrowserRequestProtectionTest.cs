using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Authentication;
using EmbedIO.Authorization;
using EmbedIO.Security;
using EmbedIO.Sessions;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using EmbedIO.Routing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class BrowserRequestProtectionTest
    {
        [TestCase("*")]
        [TestCase("null")]
        [TestCase("https://*.example.com")]
        [TestCase("https://example.com/path")]
        [TestCase("https://user@example.com")]
        [TestCase("https://example.com?query")]
        [TestCase("https://example.com#fragment")]
        [TestCase("https://example.com:")]
        [TestCase("https://example.com.")]
        [TestCase("https://example.com%2e")]
        [TestCase(" https://example.com")]
        [TestCase("https://example.com,https://other.example")]
        public void ConfigurationRejectsNonOrigins(string origin)
            => Assert.That(() => new BrowserRequestProtectionOptions(new[] { origin }), Throws.ArgumentException);

        [Test]
        public void ConfigurationCopiesListsAndBoundsLifetimeAndCounts()
        {
            var servers = new[] { "http://one.example" }; var origins = new[] { "https://ui.example" };
            var options = new BrowserRequestProtectionOptions(servers, origins);
            servers[0] = "http://evil.example"; origins[0] = "http://evil.example";
            Assert.That(options.ServerOrigins[0], Is.EqualTo("http://one.example"));
            Assert.That(options.RequestOrigins[0], Is.EqualTo("https://ui.example"));
            Assert.That(options.TokenLifetime, Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(() => new BrowserRequestProtectionOptions(Array.Empty<string>()), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new BrowserRequestProtectionOptions(Enumerable.Repeat("http://one.example", 65).ToArray()), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new BrowserRequestProtectionOptions(new[] { "http://one.example" }, tokenLifetime: TimeSpan.Zero), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new BrowserRequestProtectionOptions(new[] { "http://one.example" }, tokenLifetime: TimeSpan.FromDays(2)), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ValidationPrecedesActivationHooksAndBodyBinding(bool generated)
        {
            BrowserGuardController.Created = 0; BrowserGuardController.Hooks = 0; BrowserGuardBodyAttribute.Reads = 0;
            using var protection = Policy();
            await TestWebServer.UseAsync(server => Configure(server, protection, generated), async client =>
            {
                using var denied = await client.PostAsync("/api/protected", new StringContent("payload"));
                Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(BrowserGuardController.Created, Is.Zero); Assert.That(BrowserGuardController.Hooks, Is.Zero); Assert.That(BrowserGuardBodyAttribute.Reads, Is.Zero);
                var token = await client.GetStringAsync("/token");
                using var request = Post("/api/protected", token);
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("payload"));
                Assert.That(BrowserGuardController.Created, Is.EqualTo(1)); Assert.That(BrowserGuardController.Hooks, Is.EqualTo(1)); Assert.That(BrowserGuardBodyAttribute.Reads, Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InheritanceAnonymousAndHookSkipDoNotAccidentallyExemptTokens(bool generated)
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server => Configure(server, protection, generated), async client =>
            {
                foreach (var path in new[] { "/api/protected", "/api/anonymous", "/api/skip", "/api/override" })
                {
                    using var request = Post(path); using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), path);
                }
                using var allowed = await client.PostAsync("/api/inherited", new StringContent("payload"));
                Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using var badOrigin = Post("/api/inherited"); badOrigin.Headers.Add("Origin", "http://evil.example");
                using var rejected = await client.SendAsync(badOrigin); Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AttributesWithoutPolicyFailRegistration(bool generated)
        {
            var module = new WebApiModule("/api");
            Assert.That(() => Register(module, generated), Throws.InvalidOperationException);
            Assert.That(module.ControllerCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task UnannotatedRoutesAreProtectedOnlyWhenConfigured(bool configured)
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server =>
            {
                server.WithSessionManager(new LocalSessionManager());
                var module = new WebApiModule("/api", ResponseSerializer.None(false));
                if (configured) module.ConfigureBrowserProtection(protection);
                module.RegisterController(BrowserGuardBindings.PlainBrowserGuardController); server.WithModule(module);
            }, async client =>
            {
                using var response = await client.PostAsync("/api/plain", new StringContent("payload"));
                Assert.That(response.StatusCode, Is.EqualTo(configured ? HttpStatusCode.Forbidden : HttpStatusCode.OK));
            });
        }

        [Test]
        public async Task TokensAreUniqueNoStoreReusableAndSessionBound()
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server => Configure(server, protection), async client =>
            {
                using var issued = await client.GetAsync("/token"); var token = await issued.Content.ReadAsStringAsync();
                Assert.That(token, Has.Length.EqualTo(119)); Assert.That(issued.Headers.CacheControl?.NoStore, Is.True);
                Assert.That(await client.GetStringAsync("/token"), Is.Not.EqualTo(token));
                for (var i = 0; i < 2; i++) { using var request = Post("/api/protected", token); using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK)); }
                var testClient = (TestHttpClient)client; foreach (Cookie cookie in testClient.CookieContainer.GetCookies(client.BaseAddress ?? throw new AssertionException("No base URL."))) cookie.Expired = true;
                using var crossed = Post("/api/protected", token); using var denied = await client.SendAsync(crossed); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            });
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("null")]
        [TestCase("http://evil.example")]
        [TestCase("http://test.example.com:8081")]
        [TestCase("https://test.example.com:8080")]
        [TestCase("http://test.example.com:8080/")]
        [TestCase("http://test.example.com:8080 http://evil.example")]
        [TestCase("http://test.example.com:8080,http://evil.example")]
        public async Task SuppliedOriginMustBeOneExactTrustedOrigin(string? origin)
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server => Configure(server, protection), async client =>
            {
                var token = await client.GetStringAsync("/token"); using var request = Post("/api/protected", token);
                if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
                using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(origin == null ? HttpStatusCode.OK : HttpStatusCode.Forbidden));
            });
        }

        [TestCase("http://test.example.com:8080")]
        [TestCase("HTTP://TEST.EXAMPLE.COM:8080")]
        public async Task CanonicalTrustedOriginsAreAccepted(string origin)
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server => Configure(server, protection), async client =>
            {
                var token = await client.GetStringAsync("/token"); using var request = Post("/api/protected", token); request.Headers.Add("Origin", origin);
                using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            });
        }

        [TestCase("http://evil.example:8080/api/inherited", null)]
        [TestCase("/api/inherited", "evil.example:8080")]
        [TestCase("/api/inherited", "test.example.com:8081")]
        public async Task UntrustedTargetAndMismatchedHostAreRejectedEvenOnExemptRoutes(string path, string? host)
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server => Configure(server, protection), async client =>
            {
                using var request = Post(path); if (host != null) request.Headers.Host = host;
                using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            });
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public async Task MalformedTamperedDuplicateAndForeignPolicyTokensAreRejected(int variant)
        {
            using var protection = Policy(); using var other = Policy();
            await TestWebServer.UseAsync(server =>
            {
                Configure(server, protection); server.WithModule(new ActionModule("/other-token", HttpVerbs.Get, c => c.SendStringAsync(other.GetToken(c), "text/plain", WebServer.Utf8NoBomEncoding)));
            }, async client =>
            {
                var token = await client.GetStringAsync(variant == 4 ? "/other-token" : "/token");
                if (variant == 0) token = token.Substring(0, 118);
                if (variant == 1) token = (token[0] == 'A' ? "B" : "A") + token.Substring(1);
                if (variant == 2) token += "=";
                if (variant == 5)
                {
                    const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
                    var index = alphabet.IndexOf(token.Substring(118), StringComparison.Ordinal);
                    token = token.Substring(0, 118) + alphabet[index + 1];
                }
                using var request = Post("/api/protected", token); if (variant == 3) request.Headers.TryAddWithoutValidation(BrowserRequestProtection.TokenHeaderName, token);
                using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            });
        }

        [Test]
        public async Task DuplicateOriginsAreRejected()
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server => Configure(server, protection), async client =>
            {
                var token = await client.GetStringAsync("/token"); using var request = Post("/api/protected", token);
                request.Headers.TryAddWithoutValidation("Origin", new[] { "http://test.example.com:8080", "http://test.example.com:8080" });
                using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            });
        }

        [Test]
        public void BrowserPolicyCannotBeChangedAfterControllerRegistration()
        {
            using var policy = Policy(); var module = new WebApiModule("/api"); module.ConfigureBrowserProtection(policy);
            module.RegisterController(BrowserGuardBindings.PlainBrowserGuardController);
            Assert.That(() => module.ConfigureBrowserProtection(policy), Throws.InvalidOperationException);
            var route = BrowserGuardBindings.PlainBrowserGuardController.Routes[0];
            Assert.That(() => new WebApiRouteBinding<PlainBrowserGuardController>(route.Verb, route.Route, route.IsBaseRoute,
                route.HandlerId, route.CreateInvoker, route.Operation, route.Authorization, true, (BrowserProtectionMode)2), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public async Task ExpiredTokensAreRejected()
        {
            using var protection = new BrowserRequestProtection(new BrowserRequestProtectionOptions(new[] { TestWebServer.DefaultBaseUrl }, tokenLifetime: TimeSpan.FromMilliseconds(20)));
            await TestWebServer.UseAsync(server => Configure(server, protection), async client =>
            {
                var token = await client.GetStringAsync("/token"); await Task.Delay(60);
                using var request = Post("/api/protected", token); using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            });
        }

        [Test]
        public async Task IdentityChangesInvalidateTokensWithoutImplicitBearerExemption()
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server =>
            {
                server.WithModule(new BearerAuthenticationModule("/", (name, _) => Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, name) }, "test")))));
                Configure(server, protection);
            }, async client =>
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "alice"); var token = await client.GetStringAsync("/token");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "bob");
                using var changed = Post("/api/protected", token); using var rejected = await client.SendAsync(changed); Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                using var missing = Post("/api/protected"); using var denied = await client.SendAsync(missing); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "alice");
                using var same = Post("/api/protected", token); using var accepted = await client.SendAsync(same); Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            });
        }

        [TestCase(ClaimTypes.NameIdentifier, false)]
        [TestCase(ClaimTypes.NameIdentifier, true)]
        [TestCase(ClaimTypes.Name, false)]
        [TestCase(ClaimTypes.Name, true)]
        [TestCase("sub", false)]
        [TestCase("sub", true)]
        public async Task IdentityClaimIssuerChangesInvalidateTokens(string claimType, bool originalIssuer)
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server =>
            {
                server.WithModule(new BearerAuthenticationModule("/", (issuer, _) =>
                {
                    var claim = new Claim(claimType, "same-subject", ClaimValueTypes.String,
                        originalIssuer ? "trusted-issuer" : issuer, originalIssuer ? issuer : "original-issuer");
                    var nameType = claimType == ClaimTypes.NameIdentifier ? ClaimTypes.Name : claimType;
                    return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(new[] { claim }, "test", nameType, ClaimTypes.Role)));
                }));
                Configure(server, protection);
            }, async client =>
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "issuer-a");
                var token = await client.GetStringAsync("/token");
                using (var same = Post("/api/protected", token))
                {
                    using var accepted = await client.SendAsync(same);
                    Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                }
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "issuer-b");
                using (var changed = Post("/api/protected", token))
                {
                    using var rejected = await client.SendAsync(changed);
                    Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                }
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "issuer-a");
                using var restored = Post("/api/protected", token);
                using var response = await client.SendAsync(restored);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            });
        }

        [Test]
        public async Task SessionRegenerationInvalidatesTokens()
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server =>
            {
                server.WithModule(new ActionModule("/regenerate", HttpVerbs.Get, c => { c.Session.Regenerate(); return Task.CompletedTask; })); Configure(server, protection);
            }, async client =>
            {
                var token = await client.GetStringAsync("/token"); using var regenerate = await client.GetAsync("/regenerate"); regenerate.EnsureSuccessStatusCode();
                using var request = Post("/api/protected", token); using var response = await client.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            });
        }

        [Test]
        public async Task MiddlewareProtectsNonControllerHandlersAndSafeMethodsNeedNoSession()
        {
            using var protection = Policy(); var calls = 0;
            await TestWebServer.UseAsync(server => server.WithModule(new BrowserRequestProtectionModule("/", protection))
                .WithModule(new ActionModule("/", HttpVerbs.Any, c => { Interlocked.Increment(ref calls); return c.SendStringAsync(c.Session.Exists.ToString(), "text/plain", WebServer.Utf8NoBomEncoding); })), async client =>
            {
                Assert.That(await client.GetStringAsync("/"), Is.EqualTo("False"));
                using var denied = await client.PostAsync("/", new StringContent("payload")); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden)); Assert.That(calls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task DisposedPolicyFailsClosedAndMissingSessionManagerDoesNotIssueToken()
        {
            using var protection = Policy();
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule("/", HttpVerbs.Get, c =>
            {
                Assert.That(() => protection.GetToken(c), Throws.InvalidOperationException);
                protection.Dispose(); Assert.That(() => protection.ValidateRequest(c), Throws.TypeOf<ObjectDisposedException>());
                return Task.CompletedTask;
            })), async client => { using var response = await client.GetAsync("/"); response.EnsureSuccessStatusCode(); });
        }

        private static BrowserRequestProtection Policy() => new(new BrowserRequestProtectionOptions(new[] { TestWebServer.DefaultBaseUrl }));
        private static HttpRequestMessage Post(string path, string? token = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("payload") };
            if (token != null) request.Headers.TryAddWithoutValidation(BrowserRequestProtection.TokenHeaderName, token); return request;
        }
        private static void Configure(IWebServer server, BrowserRequestProtection protection, bool generated = true)
        {
            server.WithSessionManager(new LocalSessionManager());
            server.WithModule(new ActionModule("/token", HttpVerbs.Get, c => c.SendStringAsync(protection.GetToken(c), "text/plain", WebServer.Utf8NoBomEncoding)));
            var module = new WebApiModule("/api", ResponseSerializer.None(false)); module.ConfigureBrowserProtection(protection); Register(module, generated); server.WithModule(module);
        }
        private static void Register(WebApiModule module, bool generated)
        {
            if (generated) module.RegisterController(BrowserGuardBindings.BrowserGuardController);
            else module.RegisterController<BrowserGuardController>();
        }
    }

    [GeneratedControllerBinding(typeof(BrowserGuardController))]
    [GeneratedControllerBinding(typeof(PlainBrowserGuardController))]
    internal static partial class BrowserGuardBindings { }
    [BrowserProtection(BrowserProtectionMode.IgnoreToken)]
    public abstract class BrowserGuardBaseController : WebApiController
    {
        [Route(HttpVerbs.Post, "/inherited")]
        public virtual string Inherited([BrowserGuardBody] string value) => value;
        [Route(HttpVerbs.Post, "/override"), BrowserProtection]
        public virtual string Override([BrowserGuardBody] string value) => value;
    }
    public sealed class BrowserGuardController : BrowserGuardBaseController
    {
        public static int Created;
        public static int Hooks;
        public BrowserGuardController() => Interlocked.Increment(ref Created);
        public override string Override(string value) => value;
        protected override void OnBeforeHandler() { Interlocked.Increment(ref Hooks); base.OnBeforeHandler(); }
        [BrowserProtection]
        [Route(HttpVerbs.Post, "/protected")]
        public string Protected([BrowserGuardBody] string value) => value;
        [BrowserProtection, AllowAnonymous]
        [Route(HttpVerbs.Post, "/anonymous")]
        public string Anonymous([BrowserGuardBody] string value) => value;
        [BrowserProtection, BeforeHandler(Behavior = BeforeHandlerBehavior.Skip)]
        [Route(HttpVerbs.Post, "/skip")]
        public string Skip([BrowserGuardBody] string value) => value;
    }
    public sealed class PlainBrowserGuardController : WebApiController
    {
        [Route(HttpVerbs.Post, "/plain")]
        public string Plain() => "ok";
    }
    [AttributeUsage(AttributeTargets.Parameter, Inherited = true)]
    public sealed class BrowserGuardBodyAttribute : Attribute, IRequestDataAttribute<WebApiController, string>
    {
        public static int Reads;
        public async Task<string?> GetRequestDataAsync(WebApiController controller, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(controller); Interlocked.Increment(ref Reads); return await controller.HttpContext.GetRequestBodyAsStringAsync().ConfigureAwait(false);
        }
    }
}
