using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Serialization;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue432_BaseRoutes
    {
        [TestCase("/someurl", "/")]
        [TestCase("/someurl/", "/")]
        [TestCase("/someurl/1", "/1")]
        [TestCase("/someurl/1/2", "/1/2")]
        [TestCase("/someurl/1/2?q=kept", "/1/2")]
        public Task RootBaseRouteHandlesArbitraryDescendants(string path, string expected)
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/someurl", api => api.WithController<CatchAllController>()),
                async client => await Check(client, path, HttpStatusCode.OK, expected));

        [TestCase("/someurl/other/42", HttpStatusCode.OK, "42|/")]
        [TestCase("/someurl/other/42/", HttpStatusCode.OK, "42|/")]
        [TestCase("/someurl/other/42/1", HttpStatusCode.OK, "42|/1")]
        [TestCase("/someurl/other/42/1/2", HttpStatusCode.OK, "42|/1/2")]
        [TestCase("/someurl/other/not-a-number/1", HttpStatusCode.BadRequest, null)]
        [TestCase("/someurl/otherish/42/1", HttpStatusCode.NotFound, null)]
        [TestCase("/somewhere/other/42/1", HttpStatusCode.NotFound, null)]
        public Task ParameterizedBaseRouteBindsPrefixAndPreservesRemainder(string path, HttpStatusCode status, string? expected)
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/someurl", api => api.WithController<ParameterizedController>()),
                async client => await Check(client, path, status, expected));

        [TestCase(false, "health")]
        [TestCase(true, "/health")]
        public Task ControllerRegistrationOrderDeterminesFallbackPrecedence(bool fallbackFirst, string expected)
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/someurl", api =>
                {
                    if (fallbackFirst) api.WithController<CatchAllController>();
                    api.WithController<SpecificController>();
                    if (!fallbackFirst) api.WithController<CatchAllController>();
                }),
                async client =>
                {
                    await Check(client, "/someurl/health", HttpStatusCode.OK, expected);
                    await Check(client, "/someurl/unlisted/nested", HttpStatusCode.OK, "/unlisted/nested");
                    await Check(client, "/else/health", HttpStatusCode.NotFound);
                });

        [TestCase("GET")]
        [TestCase("POST")]
        [TestCase("PATCH")]
        [TestCase("DELETE")]
        public Task AnyVerbBaseRouteCanBeSelectedExplicitly(string method)
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/someurl", api => api.WithController<CatchAllController>()),
                async client =>
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), "/someurl/arbitrary/path");
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(Json.Deserialize<string>(await response.Content.ReadAsStringAsync()), Is.EqualTo("/arbitrary/path"));
                });

        [TestCase("GET", HttpStatusCode.OK)]
        [TestCase("POST", HttpStatusCode.MethodNotAllowed)]
        public Task GetOnlyBaseRouteDoesNotWidenAllowedMethods(string method, HttpStatusCode expected)
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/someurl", api => api.WithController<ParameterizedController>()),
                async client =>
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), "/someurl/other/42/child");
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                });

        [Test]
        public Task OriginalAsteriskAttributeIsLiteralRatherThanCatchAll()
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/someurl", api => api.WithController<AsteriskController>()),
                async client =>
                {
                    await Check(client, "/someurl/one/two", HttpStatusCode.NotFound);
                    await Check(client, "/someurl/*", HttpStatusCode.OK, "literal");
                });

        [Test]
        public void RouteRegexMetacharactersRemainLiteral()
        {
            var matcher = RouteMatcher.Parse("/a.b+", false);
            Assert.That(matcher.Match("/a.b+"), Is.Not.Null);
            Assert.That(matcher.Match("/axb"), Is.Null);
            Assert.That(matcher.Match("/abbb"), Is.Null);
        }

        [Test]
        public Task ControllerSubpathExcludesQueryAndPreservesRequestData()
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/someurl", api => api.WithController<DataController>()),
                async client => await Check(client, "/someurl/MiXeD/child?q=VaLuE",
                    HttpStatusCode.OK, "/MiXeD/child|VaLuE|/someurl/MiXeD/child"));

        private static async Task Check(HttpClient client, string path, HttpStatusCode status, string? expected = null)
        {
            using var response = await client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(status), path);
            if (expected != null)
                Assert.That(Json.Deserialize<string>(await response.Content.ReadAsStringAsync()), Is.EqualTo(expected), path);
        }

        public sealed class CatchAllController : WebApiController
        {
            [BaseRoute(HttpVerbs.Any, "/")]
            public string? CatchAll() => Route.SubPath;
        }

        public sealed class SpecificController : WebApiController
        {
            [Route(HttpVerbs.Get, "/health")]
            public string Health() => "health";
        }

        public sealed class ParameterizedController : WebApiController
        {
            [BaseRoute(HttpVerbs.Get, "/other/{id}/")]
            public string Prefix(int id) => $"{id}|{Route.SubPath}";
        }

        public sealed class AsteriskController : WebApiController
        {
            [Route(HttpVerbs.Any, "/*")]
            public string Literal() => "literal";
        }

        public sealed class DataController : WebApiController
        {
            [BaseRoute(HttpVerbs.Get, "/")]
            public string Read() => $"{Route.SubPath}|{Request.QueryString["q"]}|{Request.Url.AbsolutePath}";
        }
    }
}
