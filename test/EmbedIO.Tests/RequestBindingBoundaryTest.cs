using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestBindingBoundaryTest
    {
        [TestCase("name=first&name=last&tags[]=a&tags[]=b&count=12&numbers=2&numbers=3", "last", 12, 2, 2)]
        [TestCase("", null, 0, 0, 0)]
        public Task FormFieldsBindLastScalarArraysAndMissingDefaults(string body, string? name, int count, int tags, int numbers)
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/api", module => module.RegisterController<BindingController>()),
                async client =>
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
                    using var response = await client.PostAsync("/api/form", content);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var root = json.RootElement;
                    Assert.That(root.GetProperty("Name").GetString(), Is.EqualTo(name));
                    Assert.That(root.GetProperty("Count").GetInt32(), Is.EqualTo(count));
                    Assert.That(root.GetProperty("Tags").GetArrayLength(), Is.EqualTo(tags));
                    Assert.That(root.GetProperty("Numbers").GetArrayLength(), Is.EqualTo(numbers));
                    if (numbers > 0)
                    {
                        Assert.That(root.GetProperty("Numbers")[0].GetInt32(), Is.EqualTo(2));
                        Assert.That(root.GetProperty("Numbers")[1].GetInt32(), Is.EqualTo(3));
                        Assert.That(root.GetProperty("Tags")[0].GetString(), Is.EqualTo("a"));
                        Assert.That(root.GetProperty("Tags")[1].GetString(), Is.EqualTo("b"));
                    }
                });

        [TestCase("/api/form", "count=oops")]
        [TestCase("/api/form", "numbers=1&numbers=oops")]
        [TestCase("/api/required", "")]
        public Task InvalidOrMissingRequiredFormFieldReturnsBadRequest(string path, string body)
            => TestWebServer.UseAsync(
                server => server.WithWebApi("/api", module => module.RegisterController<BindingController>()),
                async client =>
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
                    using var response = await client.PostAsync(path, content);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                });

        public sealed class BindingController : WebApiController
        {
            [Route(HttpVerbs.Post, "/form")]
            public object Form([FormField] string? name, [FormField] string[] tags, [FormField] int count, [FormField] int[] numbers)
                => new { Name = name, Tags = tags, Count = count, Numbers = numbers };

            [Route(HttpVerbs.Post, "/required")]
            public string Required([FormField("token", true)] string token) => token;
        }
    }
}
