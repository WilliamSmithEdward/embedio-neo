using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Cors;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class CorsConfigurationRegressionTest
    {
        [TestCase("*", "GET")]
        [TestCase("X-Allowed", "*")]
        [TestCase("X-Allowed", "GET")]
        public Task WildcardOriginsAllowOrdinaryRequestsWithOtherRestrictions(string headers, string methods)
            => TestWebServer.UseAsync(server => server.WithModule(new CorsModule("/", "*", headers, methods))
                .OnAny(context => context.SendStringAsync("downstream", "text/plain", WebServer.DefaultEncoding)), async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                    request.Headers.Add(HttpHeaderNames.Origin, "https://client.example");
                    using var response = await client.SendAsync(request);
                    Assert.That(response.Headers.GetValues(HttpHeaderNames.AccessControlAllowOrigin), Is.EqualTo(new[] { "*" }));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("downstream"));
                });

        [TestCase("*", "X-Allowed", "x-allowed", "X-Allowed")]
        [TestCase("*", "X-Allowed", "x-denied", "X-Allowed")]
        [TestCase("https://client.example", "X-Allowed", "x-allowed,x-denied", "X-Allowed")]
        [TestCase("https://client.example", "*", "authorization,x-custom", "authorization,x-custom")]
        [TestCase("*", "*", "x-custom", "x-custom")]
        public Task PreflightAdvertisesConfiguredHeaders(string origins, string allowed, string requested, string expected)
            => TestWebServer.UseAsync(server => server.WithModule(new CorsModule("/", origins, allowed, "GET")), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Options, "/");
                request.Headers.Add(HttpHeaderNames.Origin, "https://client.example");
                request.Headers.Add(HttpHeaderNames.AccessControlRequestMethod, "GET");
                request.Headers.TryAddWithoutValidation(HttpHeaderNames.AccessControlRequestHeaders, requested);
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Headers.GetValues(HttpHeaderNames.AccessControlAllowOrigin), Is.EqualTo(new[] { origins }));
                Assert.That(response.Headers.GetValues(HttpHeaderNames.AccessControlAllowMethods), Is.EqualTo(new[] { "GET" }));
                Assert.That(string.Join(",", response.Headers.GetValues(HttpHeaderNames.AccessControlAllowHeaders)), Is.EqualTo(expected));
            });

        [Test]
        public Task WildcardOriginStillRejectsDisallowedPreflightMethod()
            => TestWebServer.UseAsync(server => server.WithModule(new CorsModule("/", "*", "*", "GET")), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Options, "/");
                request.Headers.Add(HttpHeaderNames.Origin, "https://client.example");
                request.Headers.Add(HttpHeaderNames.AccessControlRequestMethod, "DELETE");
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(response.Headers.Contains(HttpHeaderNames.AccessControlAllowMethods), Is.False);
            });
    }
}
