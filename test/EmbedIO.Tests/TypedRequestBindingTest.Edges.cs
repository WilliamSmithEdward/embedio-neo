using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TypedRequestBindingTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task SuccessfulReferenceParserMustProduceNonNullResult(bool generated) =>
            await WithController(generated, async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/reference");
                request.Headers.Add("X-Reference", "null");
                using var rejected = await client.SendAsync(request);
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                using var valid = new HttpRequestMessage(HttpMethod.Get, "/api/reference");
                valid.Headers.Add("X-Reference", "book");
                using var accepted = await client.SendAsync(valid);
                Assert.That(await accepted.Content.ReadAsStringAsync(), Is.EqualTo("book"));
            });

        [Test]
        public void ReflectionRejectsUnsupportedShapesAtRegistration()
        {
            var module = new WebApiModule("/api");
            Assert.Throws<InvalidOperationException>(() => module.RegisterController<BindingInitController>());
            Assert.Throws<InvalidOperationException>(() => module.RegisterController<BindingHiddenController>());
            Assert.Throws<InvalidOperationException>(() => module.RegisterController<BindingMixedController>());
        }
    }

    public sealed class BindingReference(string value)
    {
        public string Value { get; } = value;

        public static bool TryParse(string text, out BindingReference? result)
        {
            result = text == "null" ? null : new BindingReference(text);
            return true;
        }
    }

    public sealed partial class TypedBindingController
    {
        [Route(HttpVerbs.Get, "/reference")]
        public string Reference([HeaderField("X-Reference")] BindingReference value)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return (value ?? throw new ArgumentNullException(nameof(value))).Value;
        }
    }

    public sealed class BindingInitInput
    {
        public int Value { get; init; }
    }

    public sealed class BindingInitController : WebApiController
    {
        [Route(HttpVerbs.Get, "/")]
        public string Read([QueryObject] BindingInitInput value)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return value?.ToString() ?? "";
        }
    }

    public class BindingHiddenBase
    {
        public int Value { get; set; }
    }

    public sealed class BindingHiddenInput : BindingHiddenBase
    {
        public new int Value { get; set; }
    }

    public sealed class BindingHiddenController : WebApiController
    {
        [Route(HttpVerbs.Get, "/")]
        public string Read([QueryObject] BindingHiddenInput value)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return value?.ToString() ?? "";
        }
    }

    public sealed class BindingMixedController : WebApiController
    {
        [Route(HttpVerbs.Post, "/")]
        public string Read([FormObject] BindingSearch input, [JsonData] BindingSearch other)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return input?.ToString() + other?.ToString();
        }
    }
}
