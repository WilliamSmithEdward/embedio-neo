using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // If-None-Match uses the weak comparison (RFC 9110, Section 13.1.2), so a client's W/ tag
    // matches the same opaque value the server sent as a strong tag. If-Match stays strong.
    public class WeakEntityTagMatchingTest
    {
        private const string Tag = "\"abc\"";

        private static TestWebServer Server()
        {
            var server = new TestWebServer();
            server.WithAction("/", HttpVerbs.Get, context =>
            {
                if (context.Request.CheckIfNoneMatch(Tag, out var exists) && exists)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotModified;
                    context.Response.ContentLength64 = 0;
                    return Task.CompletedTask;
                }

                context.Response.Headers.Set(HttpHeaderNames.ETag, Tag);
                return context.SendStringAsync("body", "text/plain", WebServer.Utf8NoBomEncoding);
            }).Start();
            return server;
        }

        [TestCase("W/\"abc\"", HttpStatusCode.NotModified)]
        [TestCase("\"abc\"", HttpStatusCode.NotModified)]
        [TestCase("*", HttpStatusCode.NotModified)]
        [TestCase("W/\"abc\", \"other\"", HttpStatusCode.NotModified)]
        [TestCase("W/\"other\"", HttpStatusCode.OK)]
        [TestCase("\"abcd\"", HttpStatusCode.OK)]
        [TestCase("not a tag", HttpStatusCode.OK)]
        public async Task AWeakTagMatchesTheStrongTagItNames(string condition, HttpStatusCode expected)
        {
            using var server = Server();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation(HttpHeaderNames.IfNoneMatch, condition);
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(expected));
        }
    }
}
