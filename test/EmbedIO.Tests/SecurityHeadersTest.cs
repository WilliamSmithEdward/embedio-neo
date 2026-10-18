using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;
using static EmbedIO.Tests.SecurityHeadersFixtures;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed partial class SecurityHeadersTest
    {
        [TestCase("script-src 'nonce-fixed'")]
        [TestCase("script-src 'NONCE-fixed'")]
        [TestCase("script-src {nonce}")]
        [TestCase("script-src 'nonce-{other}'")]
        [TestCase("script-src 'nonce-{nonce}'\r\nInjected: yes")]
        [TestCase(" ")]
        [TestCase("script-src 'self'\u007f")]
        [TestCase("script-src 'self'\t")]
        [TestCase("script-src 'self'\u0080")]
        public void RejectsMalformedCspAndInjection(string value) =>
            Assert.That(() => new SecurityHeadersPolicy(value), Throws.ArgumentException);

        [TestCase("bad\r\nX: 1")]
        [TestCase("bad\0value")]
        [TestCase("bad\tvalue")]
        public void AllFreeFormFieldsRejectControls(string value)
        {
            Assert.That(
                () => new SecurityHeadersPolicy(contentSecurityPolicyReportOnly: value),
                Throws.ArgumentException
            );
            Assert.That(() => new SecurityHeadersPolicy(permissionsPolicy: value), Throws.ArgumentException);
        }

        [Test]
        public void ConfigurationBoundsAndHstsPreloadAreExplicit()
        {
            Assert.That(() => new SecurityHeadersPolicy(new string('x', 4097)), Throws.ArgumentException);
            Assert.That(
                () => new SecurityHeadersPolicy(
                    "script-src " + string.Concat(Enumerable.Repeat("'nonce-{nonce}' ", 100))
                ),
                Throws.ArgumentException
            );
            Assert.That(() => new SecurityHeadersPolicy(referrerPolicy: "unknown"), Throws.ArgumentException);
            Assert.That(
                () => new SecurityHeadersPolicy(frameOptions: "ALLOW-FROM https://example.com"),
                Throws.ArgumentException
            );
            Assert.That(() => new SecurityHeadersPolicy(hstsIncludeSubdomains: true), Throws.ArgumentException);
            Assert.That(
                () => new SecurityHeadersPolicy(hstsPreload: true, hstsMaxAge: TimeSpan.FromDays(1)),
                Throws.ArgumentException
            );
            Assert.That(
                () => new SecurityHeadersPolicy(hstsMaxAge: TimeSpan.FromSeconds(-1)),
                Throws.TypeOf<ArgumentOutOfRangeException>()
            );
            Assert.That(
                () => new SecurityHeadersPolicy(hstsMaxAge: TimeSpan.FromMilliseconds(500)),
                Throws.TypeOf<ArgumentOutOfRangeException>()
            );
            Assert.That(
                () => new SecurityHeadersPolicy(
                    hstsMaxAge: TimeSpan.FromDays(365),
                    hstsIncludeSubdomains: true,
                    hstsPreload: true
                ),
                Throws.Nothing
            );
        }

        [Test]
        public async Task UnconfiguredApplicationsRetainManualHeaders() =>
            await TestWebServer.UseAsync(
                server => server.WithModule(new ActionModule(c =>
                    {
                        c.Response.Headers["Content-Security-Policy"] = "default-src https://app.example";
                        return c.SendStringAsync("plain", "text/plain", WebServer.Utf8NoBomEncoding);
                    })),
                async client =>
                {
                    using var response = await client.GetAsync("/");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(
                            Header(response, "Content-Security-Policy"),
                            Is.EqualTo("default-src https://app.example")
                        );
                        Assert.That(Header(response, "X-Content-Type-Options"), Is.Empty);
                        Assert.That(Header(response, "Cache-Control"), Is.Empty);
                    }
                }
            );

        [TestCase(false)]
        [TestCase(true)]
        public async Task NoncesAreFreshStableAndUncacheableEvenAfterLateMutation(bool lateSelection)
        {
            var policy = NoncePolicy();
            var cache = new OutputCache();
            var calls = 0;
            await TestWebServer.UseAsync(
                server =>
                {
                    if (!lateSelection) server.WithModule(new SecurityHeadersModule("/", policy));

                    server.WithModule(
                        new ActionModule(
                            c => cache.ExecuteAsync(c, new OutputCachePolicy(TimeSpan.FromMinutes(1)), async staged =>
                            {
                                if (lateSelection) policy.Apply(staged);

                                Interlocked.Increment(ref calls);
                                var nonce = staged.GetSecurityHeadersNonce();
                                Assert.That(staged.GetSecurityHeadersNonce(), Is.EqualTo(nonce));
                                staged.Response.Headers["Content-Security-Policy"] = "default-src *";
                                staged.Response.Headers["Cache-Control"] = "public, max-age=600";
                                staged.Response.Headers["ETag"] = "\"stale\"";
                                await staged.SendStringAsync(nonce, "text/plain", WebServer.Utf8NoBomEncoding);
                            })
                        )
                    );
                },
                async client =>
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);

                    for (var i = 0; i < 12; i++)
                    {
                        using var response = await client.GetAsync("/");
                        var nonce = await response.Content.ReadAsStringAsync();

                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                            Assert.That(Convert.FromBase64String(nonce), Has.Length.EqualTo(32));
                            Assert.That(seen.Add(nonce), Is.True);
                            Assert.That(
                                Header(response, "Content-Security-Policy"),
                                Does.Contain("'nonce-" + nonce + "'")
                            );
                            Assert.That(
                                Header(response, "Content-Security-Policy-Report-Only"),
                                Is.EqualTo("script-src 'nonce-" + nonce + "'")
                            );
                            Assert.That(Header(response, "Cache-Control"), Is.EqualTo("no-store"));
                            Assert.That(Header(response, "ETag"), Is.Empty);
                        }
                    }

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(calls, Is.EqualTo(12));
                        Assert.That(cache.Count, Is.Zero);
                    }
                }
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task StaticPoliciesAllowCachingAndOverridesReplaceInheritedFields(bool generated)
        {
            SecurityHeaderController.Calls = 0;
            await TestWebServer.UseAsync(
                server =>
                {
                    server.WithModule(
                        new SecurityHeadersModule("/", new SecurityHeadersPolicy("default-src https://server.example"))
                    );
                    var module = new WebApiModule("/api", ResponseSerializer.None(false));
                    module.ConfigureSecurityHeaders(new Dictionary<string, SecurityHeadersPolicy>
                    {
                        ["class"] = new("default-src https://class.example", permissionsPolicy: "camera=()"),
                        ["action"] = new("default-src https://action.example", frameOptions: null),
                        ["nonce"] = NoncePolicy()
                    });
                    module.ConfigureOutputCaching(
                        new OutputCache(),
                        new Dictionary<string, OutputCachePolicy> { ["cache"] = new(TimeSpan.FromMinutes(1)) }
                    );

                    if (generated) module.RegisterController(SecurityHeaderBindings.SecurityHeaderController);

                    else module.RegisterController<SecurityHeaderController>();

                    server.WithModule(module);
                },
                async client =>
                {
                    for (var i = 0; i < 2; i++)
                    {
                        using var inherited = await client.GetAsync("/api/class");
                        Assert.That(
                            Header(inherited, "Content-Security-Policy"),
                            Is.EqualTo("default-src https://class.example")
                        );
                        using var action = await client.GetAsync("/api/action");

                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(
                                Header(action, "Content-Security-Policy"),
                                Is.EqualTo("default-src https://action.example")
                            );
                            Assert.That(Header(action, "Permissions-Policy"), Is.Empty);
                            Assert.That(Header(action, "X-Frame-Options"), Is.Empty);
                        }
                    }

                    Assert.That(SecurityHeaderController.Calls, Is.EqualTo(2));
                    using var nonce = await client.GetAsync("/api/nonce");
                    var body = await nonce.Content.ReadAsStringAsync();
                    Assert.That(Header(nonce, "Content-Security-Policy"), Does.Contain("'nonce-" + body + "'"));
                }
            );
        }

        [Test]
        public void UnknownControllerPolicyFailsBeforeRegistration()
        {
            var module = new WebApiModule("/");
            Assert.That(
                () => module.RegisterController(SecurityHeaderBindings.SecurityHeaderController),
                Throws.InvalidOperationException
            );
            Assert.That(module.ControllerCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SimulatedSchemesAndForwardedHeadersDoNotInventTls(bool secure)
        {
            using var server = new TestWebServer(secure ? "https://example.com/" : "http://example.com/");
            server.WithModule(
                new SecurityHeadersModule("/", new SecurityHeadersPolicy(hstsMaxAge: TimeSpan.FromSeconds(42)))
            );
            server.WithModule(
                new ActionModule(c => c.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding))
            );
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);

            try
            {
                server.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
                using var response = await server.Client.GetAsync("/");
                Assert.That(Header(response, "Strict-Transport-Security"), Is.Empty);
            }

            finally
            {
                stop.Cancel();
                await running;
            }
        }

        [Test]
        public async Task PolicyCannotChangeAfterNonceExposure() =>
            await TestWebServer.UseAsync(
                server => server.WithModule(new ActionModule(async c =>
                    {
                        NoncePolicy().Apply(c);
                        var nonce = c.GetSecurityHeadersNonce();
                        Assert.That(() => new SecurityHeadersPolicy().Apply(c), Throws.InvalidOperationException);
                        await c.SendStringAsync(nonce, "text/plain", WebServer.Utf8NoBomEncoding);
                    })),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                }
            );
    }

    [GeneratedControllerBinding(typeof(SecurityHeaderController))]
    internal static partial class SecurityHeaderBindings { }

    [SecurityHeaders("class")]
    public sealed class SecurityHeaderController : WebApiController
    {
        internal static int Calls;

        [Route(HttpVerbs.Get, "/class")]
        [OutputCache("cache")]
        public string Inherited()
        {
            Interlocked.Increment(ref Calls);
            HttpContext.Response.ContentType = "text/plain";
            return "class";
        }

        [Route(HttpVerbs.Get, "/action")]
        [SecurityHeaders("action")]
        [OutputCache("cache")]
        public string Action()
        {
            Interlocked.Increment(ref Calls);
            HttpContext.Response.ContentType = "text/plain";
            return "action";
        }

        [Route(HttpVerbs.Get, "/nonce")]
        [SecurityHeaders("nonce")]
        [OutputCache("cache")]
        public string Nonce() => HttpContext.GetSecurityHeadersNonce();
    }
}
