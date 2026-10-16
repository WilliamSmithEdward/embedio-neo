using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Authentication;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class BearerAuthenticationTest
    {
        [TestCase(null, 200, 0)]
        [TestCase("Basic abc", 200, 0)]
        [TestCase("Bearer alice", 200, 1)]
        [TestCase("bEaReR   alice", 200, 1)]
        [TestCase("Bearer bad", 401, 1)]
        [TestCase("Bearer", 400, 0)]
        [TestCase("Bearer ", 400, 0)]
        [TestCase("Bearer =", 400, 0)]
        [TestCase("Bearer alice bob", 400, 0)]
        [TestCase("Bearer alice,other", 400, 0)]
        [TestCase("Bearer alice=tail", 400, 0)]
        [TestCase("Bearer alice==", 401, 1)]
        [TestCase("Bearer a-b.c_d~e+f/g", 401, 1)]
        public async Task CredentialsHaveBoundedSyntaxAndValidation(string? header, int status, int calls)
        {
            var invoked = 0;
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (token, _) =>
            {
                invoked++;
                return Task.FromResult(token == "alice" ? Principal("alice") : null);
            }));
            server.WithModule(new ActionModule("/", HttpVerbs.Any, context => context.SendStringAsync(context.User.Identity?.Name ?? "anonymous", "text/plain", WebServer.Utf8NoBomEncoding))).Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            if (header != null) request.Headers.TryAddWithoutValidation("Authorization", header);
            using var response = await server.Client.SendAsync(request);
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
                Assert.That(invoked, Is.EqualTo(calls));
            }
            if (status == 401) Assert.That(response.Headers.WwwAuthenticate.Single().Scheme, Is.EqualTo("Bearer"));
        }

        [TestCase(null)]
        [TestCase("Basic abc")]
        public async Task ExplicitRequiredModeChallengesMissingCredentials(string? header)
        {
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => Task.FromResult<ClaimsPrincipal?>(null)) { RequireAuthentication = true }).Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            if (header != null) request.Headers.TryAddWithoutValidation("Authorization", header);
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate.Single().ToString(), Is.EqualTo("Bearer"));
        }

        [Test]
        public async Task DuplicateCredentialsDoNotReachTheValidator()
        {
            var invoked = false;
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => { invoked = true; return Task.FromResult<ClaimsPrincipal?>(Principal("alice")); })).Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer alice", "Bearer bob" });
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(invoked, Is.False);
        }

        [Test]
        public async Task CredentialLengthIsCheckedBeforeValidation()
        {
            var invoked = false;
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => { invoked = true; return Task.FromResult<ClaimsPrincipal?>(null); }) { MaximumTokenLength = 4 }).Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer alice");
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(invoked, Is.False);
        }

        [Test]
        public async Task CachedValidatorIdentitiesAreClonedForEveryRequest()
        {
            var cached = Principal("alice");
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => Task.FromResult<ClaimsPrincipal?>(cached)));
            server.WithModule(new ActionModule("/", HttpVerbs.Any, context =>
            {
                var identity = context.User.Identity as ClaimsIdentity ?? throw new InvalidOperationException("Missing claims identity.");
                var count = identity.Claims.Count();
                identity.AddClaim(new Claim("request-only", "value"));
                return context.SendStringAsync(count.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", WebServer.Utf8NoBomEncoding);
            })).Start();
            for (var index = 0; index < 3; index++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer alice");
                using var response = await server.Client.SendAsync(request);
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("1"));
            }
            Assert.That(cached.Claims.Count(), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InvalidOrUnauthenticatedPrincipalsAreRejected(bool empty)
        {
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => Task.FromResult<ClaimsPrincipal?>(empty ? new ClaimsPrincipal() : new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "alice") }))))).Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer alice");
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        [Test]
        public async Task AnonymousRequestPrincipalsAreNotShared()
        {
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => Task.FromResult<ClaimsPrincipal?>(null)));
            server.WithModule(new ActionModule("/", HttpVerbs.Any, context =>
            {
                var principal = context.User as ClaimsPrincipal ?? throw new InvalidOperationException("Missing claims principal.");
                Assert.That(principal.Identities.Any(identity => identity.IsAuthenticated), Is.False);
                principal.AddIdentity(new ClaimsIdentity("request-local"));
                return context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
            })).Start();
            for (var index = 0; index < 3; index++)
            {
                using var response = await server.Client.GetAsync("/");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
        }

        [Test]
        public async Task UnauthenticatedIdentitiesCannotContributeRolesOrClaims()
        {
            var mixed = Principal("alice");
            mixed.AddIdentity(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "admin"), new Claim("permission", "write") }));
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => Task.FromResult<ClaimsPrincipal?>(mixed)));
            server.WithModule(new ActionModule("/", HttpVerbs.Any, context =>
            {
                var principal = context.User as ClaimsPrincipal ?? throw new InvalidOperationException("Missing claims principal.");
                Assert.That(principal.IsInRole("admin"), Is.False);
                Assert.That(principal.HasClaim("permission", "write"), Is.False);
                return context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
            })).Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer alice");
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task ValidatorFaultsAreNotReportedAsInvalidCredentials()
        {
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => throw new InvalidOperationException("test backend failure"))).Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer alice");
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        }

        [Test]
        public async Task QueryAndBodyCredentialsAreNotRead()
        {
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => throw new InvalidOperationException("Unexpected validation")) { RequireAuthentication = true }).Start();
            using var response = await server.Client.PostAsync("/?access_token=alice", new StringContent("access_token=alice"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        [TestCase("say \"hello\"", "Bearer realm=\"say \\\"hello\\\"\"")]
        [TestCase("back\\slash", "Bearer realm=\"back\\\\slash\"")]
        public async Task RealmUsesQuotedStringEscaping(string realm, string expected)
        {
            using var server = new TestWebServer();
            server.WithModule(new BearerAuthenticationModule("/", (_, _) => Task.FromResult<ClaimsPrincipal?>(null)) { Realm = realm, RequireAuthentication = true }).Start();
            using var response = await server.Client.GetAsync("/");
            Assert.That(response.Headers.WwwAuthenticate.Single().ToString(), Is.EqualTo(expected));
        }

        [TestCase("bad\r\nheader")]
        [TestCase("bad\tvalue")]
        [TestCase("nonasciié")]
        public void InvalidRealmsAreRejectedDuringConfiguration(string realm)
        {
            var module = new BearerAuthenticationModule("/", (_, _) => Task.FromResult<ClaimsPrincipal?>(null));
            Assert.Throws<ArgumentException>(() => module.Realm = realm);
        }

        [Test]
        public void ConfigurationLocksWhenStarted()
        {
            var module = new BearerAuthenticationModule("/", (_, _) => Task.FromResult<ClaimsPrincipal?>(null));
            module.Start(CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => module.RequireAuthentication = true);
            Assert.Throws<InvalidOperationException>(() => module.MaximumTokenLength = 1);
            Assert.Throws<InvalidOperationException>(() => module.Realm = "changed");
        }

        private static ClaimsPrincipal Principal(string name) => new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }, "Bearer"));
    }
}
