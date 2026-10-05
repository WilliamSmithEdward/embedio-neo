using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Authentication;
using EmbedIO.Cors;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpModuleBoundaryTest
    {
        [TestCase(true, HttpStatusCode.TemporaryRedirect)]
        [TestCase(false, HttpStatusCode.OK)]
        public Task ConditionalRedirectHandlesOrPassesThrough(bool redirect, HttpStatusCode expected)
            => TestWebServer.UseAsync(
                server => server.WithModule(new RedirectModule("/", "https://example.com/target", _ => redirect, HttpStatusCode.TemporaryRedirect))
                    .OnAny(context => context.SendStringAsync("downstream", "text/plain", Encoding.UTF8)),
                async client =>
                {
                    using var response = await client.GetAsync("/");
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                    if (redirect)
                        Assert.That(response.Headers.Location, Is.EqualTo(new Uri("https://example.com/target")));
                    else
                    {
                        Assert.That(response.Headers.Location, Is.Null);
                        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("downstream"));
                    }
                });

        [TestCase(HttpStatusCode.OK)]
        [TestCase(HttpStatusCode.BadRequest)]
        public void RedirectRejectsNonRedirectStatus(HttpStatusCode status)
            => Assert.Throws<ArgumentException>(() => new RedirectModule("/", "https://example.com/", status));

        [TestCase("Basic %%%")]
        [TestCase("Basic")]
        [TestCase("Bearer token")]
        [TestCase("Basic dXNlcjp3cm9uZw==")]
        public Task MalformedOrWrongAuthenticationCannotReachProtectedHandler(string authorization)
            => TestWebServer.UseAsync(
                server => server.WithModule(new BasicAuthenticationModule("/").WithAccount("user", "pass:word"))
                    .OnAny(context => context.SendStringAsync("protected", "text/plain", Encoding.UTF8)),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                    request.Headers.TryAddWithoutValidation(HttpHeaderNames.Authorization, authorization);
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    Assert.That(response.Headers.WwwAuthenticate.ToString(), Does.StartWith("Basic realm="));
                    Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("protected"));
                });

        [Test]
        public Task AuthenticationSplitsAtFirstColonAndDecodesUtf8()
            => TestWebServer.UseAsync(
                server => server.WithModule(new BasicAuthenticationModule("/").WithAccount("élève", "pass:word"))
                    .OnAny(context => context.SendStringAsync("protected", "text/plain", Encoding.UTF8)),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                    request.Headers.TryAddWithoutValidation(HttpHeaderNames.Authorization,
                        "bAsIc " + Convert.ToBase64String(Encoding.UTF8.GetBytes("élève:pass:word")));
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("protected"));
                });

        [TestCase("https://allowed.example", "POST", HttpStatusCode.OK, true)]
        [TestCase("https://allowed.example", "DELETE", HttpStatusCode.BadRequest, true)]
        [TestCase("https://other.example", "POST", HttpStatusCode.NotFound, false)]
        public Task RestrictedCorsPreflightChecksOriginAndMethod(string origin, string method, HttpStatusCode expected, bool allowOrigin)
            => TestWebServer.UseAsync(
                server => server.WithModule(new CorsModule("/", "https://allowed.example", "content-type", "get,post")),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Options, "/");
                    request.Headers.Add(HttpHeaderNames.Origin, origin);
                    request.Headers.Add(HttpHeaderNames.AccessControlRequestMethod, method);
                    request.Headers.Add(HttpHeaderNames.AccessControlRequestHeaders, "content-type");
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                    Assert.That(response.Headers.Contains(HttpHeaderNames.AccessControlAllowOrigin), Is.EqualTo(allowOrigin));
                    if (expected == HttpStatusCode.OK)
                    {
                        Assert.That(response.Headers.GetValues(HttpHeaderNames.AccessControlAllowOrigin), Is.EqualTo(new[] { origin }));
                        Assert.That(response.Headers.GetValues(HttpHeaderNames.AccessControlAllowMethods), Is.EqualTo(new[] { method }));
                        Assert.That(response.Headers.GetValues(HttpHeaderNames.AccessControlAllowHeaders), Is.EqualTo(new[] { "content-type" }));
                    }
                });

        [Test]
        public Task WildcardCorsAllowsPreflightAndOrdinaryRequestContinues()
            => TestWebServer.UseAsync(
                server => server.WithCors().OnAny(context => context.SendStringAsync("downstream", "text/plain", Encoding.UTF8)),
                async client =>
                {
                    using var options = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/"));
                    Assert.That(options.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await options.Content.ReadAsStringAsync(), Is.Empty);
                    using var get = await client.GetAsync("/");
                    Assert.That(get.Headers.GetValues(HttpHeaderNames.AccessControlAllowOrigin), Is.EqualTo(new[] { "*" }));
                    Assert.That(await get.Content.ReadAsStringAsync(), Is.EqualTo("downstream"));
                });
    }
}
