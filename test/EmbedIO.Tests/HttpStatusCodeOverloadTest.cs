using System;
using System.Net;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class HttpStatusCodeOverloadTest
    {
        [TestCase(false, "empty", HttpStatusCode.Accepted)]
        [TestCase(true, "empty", HttpStatusCode.Accepted)]
        [TestCase(false, "html", HttpStatusCode.BadRequest)]
        [TestCase(true, "html", HttpStatusCode.BadRequest)]
        [TestCase(false, "callback", HttpStatusCode.Conflict)]
        [TestCase(true, "callback", HttpStatusCode.Conflict)]
        [TestCase(false, "redirect", HttpStatusCode.TemporaryRedirect)]
        [TestCase(true, "redirect", HttpStatusCode.TemporaryRedirect)]
        [TestCase(false, "custom", (HttpStatusCode)499)]
        [TestCase(true, "custom", (HttpStatusCode)499)]
        public async Task EnumAndIntegerHelpersHaveTheSameResponse(bool enums, string path, HttpStatusCode status)
        {
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule(async context =>
            {
                switch (path)
                {
                    case "empty":
                        if (enums) context.Response.SetEmptyResponse(status); else context.Response.SetEmptyResponse((int)status);
                        break;
                    case "html":
                        if (enums) await context.SendStandardHtmlAsync(status); else await context.SendStandardHtmlAsync((int)status);
                        break;
                    case "callback":
                        if (enums) await context.SendStandardHtmlAsync(status, writer => writer.Write("extra"));
                        else await context.SendStandardHtmlAsync((int)status, writer => writer.Write("extra"));
                        break;
                    case "redirect":
                        if (enums) context.Redirect("/destination", status); else context.Redirect("/destination", (int)status);
                        break;
                    default:
                        if (enums) context.Response.SetStatusCode(status); else context.Response.SetStatusCode((int)status);
                        break;
                }
            })), async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That(response.StatusCode, Is.EqualTo(status));
                var body = await response.Content.ReadAsStringAsync();
                if (path == "callback") Assert.That(body, Does.Contain("extra"));
                if (path == "html") Assert.That(body, Does.Contain("400 - Bad Request"));
                if (path == "redirect") Assert.That(response.Headers.Location?.AbsolutePath, Is.EqualTo("/destination"));
            });
        }

        [TestCase(399)]
        [TestCase(600)]
        public void ProblemDetailsRetainsIntegerValidation(int status)
        {
            Assert.That(() => new ProblemDetails(status), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new ProblemDetails((HttpStatusCode)status), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [TestCase(HttpStatusCode.BadRequest)]
        [TestCase(HttpStatusCode.Conflict)]
        [TestCase((HttpStatusCode)499)]
        public void EnumMetadataKeepsTheNumericStatus(HttpStatusCode status)
        {
            Assert.That(new ProblemDetails(status).Status, Is.EqualTo((int)status));
            Assert.That(new OpenApiResponseAttribute(status).StatusCode, Is.EqualTo((int)status));
            var document = new OpenApiDocumentBuilder("Statuses", "1")
                .AddOperation(HttpVerbs.Get, "/value", operation => operation.Response(status, "custom"))
                .Build(new System.Text.Json.JsonSerializerOptions());
            Assert.That(document.Root["paths"]?["/value"]?["get"]?["responses"]?[(int)status + ""]?["description"]?.GetValue<string>(), Is.EqualTo("custom"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EnumOverloadsRetainInvalidRedirectAndEmptyStatusErrors(bool enums)
        {
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule(context =>
            {
                Assert.That(() => { if (enums) context.Redirect("/next", HttpStatusCode.OK); else context.Redirect("/next", 200); }, Throws.ArgumentException);
                Assert.That(() => { if (enums) context.Response.SetEmptyResponse((HttpStatusCode)499); else context.Response.SetEmptyResponse(499); }, Throws.ArgumentException);
                context.Response.SetEmptyResponse(HttpStatusCode.NoContent);
                return Task.CompletedTask;
            })), async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            });
        }
    }
}
