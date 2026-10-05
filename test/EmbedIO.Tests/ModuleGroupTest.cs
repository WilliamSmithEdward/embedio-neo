using System.Net;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ModuleGroupTest
    {
        [Test]
        public Task NestedGroupsUseRelativeRoutesAndDoNotMatchSiblingPrefixes()
            => TestWebServer.UseAsync(
                server => server.WithModule(new ModuleGroup("/outer", false)
                    .WithModule(new ModuleGroup("/inner", false)
                        .OnGet("/value", context => context.SendStringAsync("nested", "text/plain", Encoding.UTF8)))),
                async client =>
                {
                    Assert.That(await client.GetStringAsync("/outer/inner/value"), Is.EqualTo("nested"));
                    using var outside = await client.GetAsync("/outer-other/inner/value");
                    Assert.That(outside.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                    using var missing = await client.GetAsync("/outer/inner/missing");
                    Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                });

        [TestCase(false)]
        [TestCase(true)]
        public Task FinalGroupControlsFallthrough(bool final)
            => TestWebServer.UseAsync(
                server => server.WithModule(new ModuleGroup("/group", final))
                    .OnAny(context => context.SendStringAsync("fallback", "text/plain", Encoding.UTF8)),
                async client =>
                {
                    using var response = await client.GetAsync("/group/missing");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(final ? "" : "fallback"));
                });
    }
}
