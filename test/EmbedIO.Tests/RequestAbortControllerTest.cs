using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.Serialization;
using EmbedIO.WebApi;
using NUnit.Framework;
using RunningServer = EmbedIO.Tests.Issues.Issue138_GeneratedBinding.RunningServer;

namespace EmbedIO.Tests
{
    [GeneratedControllerBinding(typeof(RequestAbortController))]
    [GeneratedControllerBinding(typeof(RequestAbortBareController))]
    internal static partial class RequestAbortBindings { }

    public sealed class RequestAbortController : WebApiController
    {
        [Route(HttpVerbs.Get, "/normal")]
        public string Normal([RequestAborted] CancellationToken token)
            => token.CanBeCanceled && token == HttpContext.GetRequestAbortedToken() ? "normal" : "wrong";
        [Route(HttpVerbs.Get, "/input-end")]
        public string InputEnd([RequestAborted(CancelOnHttp1InputEnd = true)] CancellationToken token)
            => token == HttpContext.GetRequestAbortedToken(true) && token != HttpContext.GetRequestAbortedToken() ? "input-end" : "wrong";
    }
    public sealed class RequestAbortBareController : WebApiController
    {
        [Route(HttpVerbs.Get, "/bare")]
        public string Bare(CancellationToken token) => token.CanBeCanceled ? "changed" : "unchanged";
    }
    public sealed class RequestAbortInvalidController : WebApiController
    {
        [Route(HttpVerbs.Get, "/")]
        public string Invalid([RequestAborted] int token) => token.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public class RequestAbortControllerTest
    {
        [TestCase(false, "normal", "normal")]
        [TestCase(true, "normal", "normal")]
        [TestCase(false, "input-end", "input-end")]
        [TestCase(true, "input-end", "input-end")]
        [TestCase(false, "bare", "unchanged")]
        [TestCase(true, "bare", "unchanged")]
        public async Task ReflectionAndGeneratedBindingsPreserveTokenSemantics(bool generated, string path, string expected)
        {
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated) module.WithController(RequestAbortBindings.RequestAbortController).WithController(RequestAbortBindings.RequestAbortBareController);
            else module.WithController<RequestAbortController>().WithController<RequestAbortBareController>();
            await using var server = RunningServer.Start(module);
            using var client = new HttpClient();
            Assert.That(await client.GetStringAsync(server.Url + "api/" + path), Is.EqualTo(expected));
        }
        [Test]
        public void ReflectionRejectsInvalidTokenParameterAtRegistration()
            => Assert.That(() => new WebApiModule("/").WithController<RequestAbortInvalidController>(), Throws.InvalidOperationException);
        [Test]
        public void OpenApiOmitsTheInfrastructureToken()
        {
            var document = new OpenApiDocumentBuilder("Abort", "1").AddController("/api", RequestAbortBindings.RequestAbortController).Build(Json.CreateOptions());
            Assert.That(document.Problems, Is.Empty);
            Assert.That(document.Root["paths"]?["/api/normal"]?["get"]?["parameters"], Is.Null);
            Assert.That(document.Root["paths"]?["/api/input-end"]?["get"]?["parameters"], Is.Null);
        }
    }
}
