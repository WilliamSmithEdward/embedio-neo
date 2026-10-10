using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        [Test]
        public async Task AdapterEarlyHintsDoNotCommitOrReplaceFinalResponseState()
        {
            await WithServer(async exchange =>
            {
                var context = Adapter(exchange);
                try
                {
                    context.Response.StatusCode = 201;
                    context.Response.ContentLength64 = 3;
                    context.Response.Headers["X-Final"] = "retained";
                    var method = context.Response.GetType().GetMethod("SendInformationalAsync", Flags)
                        ?? throw new AssertionException("Missing adapter informational writer.");
                    foreach (var path in new[] { "/one", "/two" })
                        await (Task)(method.Invoke(context.Response, new object[]
                        { 103, new WebHeaderCollection { ["Link"] = "<" + path + ">; rel=preload" }, context.CancellationToken })
                            ?? throw new AssertionException("Missing task."));
                    Assert.That(context.Response.StatusCode, Is.EqualTo(201));
                    Assert.That(context.Response.Headers["Link"], Is.Null);
                    await context.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 }, context.CancellationToken);
                }
                finally { context.Close(); }
            }, async client =>
            {
                using var response = await client.GetAsync("hints");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                Assert.That(response.Headers.GetValues("x-final"), Is.EqualTo(new[] { "retained" }));
                Assert.That(response.Headers.Contains("link"), Is.False);
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
        }
    }
}
