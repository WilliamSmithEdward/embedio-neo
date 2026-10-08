using System.Net;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RoutingModuleHandlerTest
    {
        [TestCase("GET", "/api/value/42", HttpStatusCode.OK, "get:42")]
        [TestCase("POST", "/api/value/42", HttpStatusCode.Created, "")]
        [TestCase("DELETE", "/api/value/42", HttpStatusCode.MethodNotAllowed, null)]
        [TestCase("GET", "/api/missing", HttpStatusCode.NotFound, null)]
        public Task SyncAndAsyncHandlersResolvePathAndVerb(string method, string path, HttpStatusCode expected, string? body)
            => TestWebServer.UseAsync(
                server => server.WithModule(new RoutingModule("/api")
                    .Handle(HttpVerbs.Get, "/value/{id}", (context, route) =>
                        context.SendStringAsync("get:" + route["id"], "text/plain", Encoding.UTF8))
                    .Handle(HttpVerbs.Post, "/value/{id}", (context, _) =>
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Created;
                    })),
                async client =>
                {
                    using var request = new System.Net.Http.HttpRequestMessage(new System.Net.Http.HttpMethod(method), path);
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                    if (body != null)
                        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(body));
                });

        [Test]
        public Task BaseRouteHandlerReceivesRemainingPathAndParameters()
            => TestWebServer.UseAsync(
                server => server.WithModule(new RoutingModule("/api")
                    .Handle(HttpVerbs.Get, "/files/{owner}", true, (context, route) =>
                        context.SendStringAsync(route["owner"] + ":" + route.SubPath, "text/plain", Encoding.UTF8))),
                async client => Assert.That(await client.GetStringAsync("/api/files/alice/sub/file.txt"), Is.EqualTo("alice:/sub/file.txt")));

        [Test]
        public Task AttributedSyncAndAsyncHandlersAreDiscovered()
            => TestWebServer.UseAsync(
                server =>
                {
                    var module = new RoutingModule("/api");
                    Assert.That(module.AddFrom(new Handlers()), Is.EqualTo(2));
                    server.WithModule(module);
                },
                async client =>
                {
                    Assert.That(await client.GetStringAsync("/api/async/7"), Is.EqualTo("7"));
                    using var response = await client.GetAsync("/api/sync");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
                });

        public sealed class Handlers
        {
            [Route(HttpVerbs.Get, "/async/{id}")]
            public Task Async(IHttpContext context, RouteMatch route)
            {
                if (route is null) throw new System.NullReferenceException();
                return context.SendStringAsync(route["id"], "text/plain", Encoding.UTF8);
            }

            [Route(HttpVerbs.Get, "/sync")]
            public void Sync(IHttpContext context, RouteMatch route)
            {
                if (context is null) throw new System.NullReferenceException();
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
            }
        }
    }
}
