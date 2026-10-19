using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authorization;
using EmbedIO.Endpoints;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed partial class MinimalEndpointsTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ExactNestedRoutesBindAllSourcesAndPreserveFallback(bool generated) =>
            await TestWebServer.UseAsync(
                server =>
                {
                    server.WithProblemDetails();
                    var group = server
                        .MapGroup("/api")
                        .MapGroup("/books/{tenant}");
                    group.MapGet(
                        "/{id}",
                        generated
                            ? MinimalHandlers.ReadEndpoint
                            : EndpointHandler.FromDelegate(
                                (Func<string, int, MinimalSearch, string, string, OkResult<string>>)MinimalHandlers.Read
                            )
                    );
                    group.MapPost(
                        "/form",
                        generated
                            ? MinimalHandlers.FormEndpoint
                            : EndpointHandler.FromDelegate((Func<MinimalSearch, OkResult<string>>)MinimalHandlers.Form)
                    );
                    group.MapPost(
                        "/json",
                        generated
                            ? MinimalHandlers.JsonEndpoint
                            : EndpointHandler.FromDelegate((Func<MinimalSearch, OkResult<string>>)MinimalHandlers.Json)
                    );
                    server.OnGet("/fallback", c => c.SendStringAsync("later", "text/plain", System.Text.Encoding.UTF8));
                },
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/books/team/4?Page=3");
                    request.Headers.Add("X-Label", "header");
                    request.Headers.Add("Cookie", "theme=dark");
                    using var response = await client.SendAsync(request);
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("\"team|4|3|header|dark\""));
                    using var extra = await client.GetAsync("/api/books/team/4/extra");
                    Assert.That(extra.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                    using var method = await client.PostAsync("/api/books/team/4", null);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(method.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
                        Assert.That(await client.GetStringAsync("/fallback"), Is.EqualTo("later"));
                    }

                    using var form = await client.PostAsync(
                        "/api/books/team/form",
                        new FormUrlEncodedContent(new Dictionary<string, string> { ["Page"] = "7" })
                    );
                    Assert.That(await form.Content.ReadAsStringAsync(), Is.EqualTo("\"7\""));
                    using var json = await client.PostAsync(
                        "/api/books/team/json",
                        new StringContent("{\"Page\":8}", System.Text.Encoding.UTF8, "application/json")
                    );
                    Assert.That(await json.Content.ReadAsStringAsync(), Is.EqualTo("\"8\""));
                }
            );

        [TestCase(false, "?Page=0")]
        [TestCase(true, "?Page=0")]
        [TestCase(false, "?Page=2&Page=3")]
        [TestCase(true, "?Page=2&Page=3")]
        [TestCase(false, "?Page=%FF")]
        [TestCase(true, "?Page=%FF")]
        public async Task InvalidBindingRejectsSafelyAndRecovers(bool generated, string query) =>
            await TestWebServer.UseAsync(
                server =>
                {
                    server.WithProblemDetails();
                    server.MapGet(
                        "/search",
                        generated
                            ? MinimalHandlers.SearchEndpoint
                            : EndpointHandler.FromDelegate(
                                (Func<MinimalSearch, OkResult<string>>)MinimalHandlers.Search
                            )
                    );
                },
                async client =>
                {
                    using var bad = await client.GetAsync("/search" + query);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                        Assert.That(await client.GetStringAsync("/search?Page=3"), Is.EqualTo("\"3\""));
                    }
                }
            );

        private static readonly string[] ExpectedBeforeCalls = new[] { "before" };

        [TestCase(false)]
        [TestCase(true)]
        public async Task AuthorizationRunsBeforeHooksAndBindingWithExplicitAnonymousOverride(bool generated)
        {
            var calls = new List<string>();
            await TestWebServer.UseAsync(
                server =>
                {
                    server.WithProblemDetails();
                    var group = server.MapGroup("/api");
                    group.OnBeforeHandler(_ =>
                    {
                        calls.Add("before");
                        return Task.CompletedTask;
                    });
                    group.MapGet(
                        "/private",
                        generated
                            ? MinimalHandlers.SearchEndpoint
                            : EndpointHandler.FromDelegate(
                                (Func<MinimalSearch, OkResult<string>>)MinimalHandlers.Search
                            )
                    );
                    group
                        .MapGet("/public", MinimalHandlers.StatusEndpoint)
                        .AllowAnonymous();
                    group.RequireAuthorization(); // Applied even when configured after endpoint registration.
                },
                async client =>
                {
                    using var denied = await client.GetAsync("/api/private?Page=bad");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                        Assert.That(calls, Is.Empty);
                    }

                    using var allowed = await client.GetAsync("/api/public");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        Assert.That(calls, Is.EqualTo(ExpectedBeforeCalls));
                    }
                }
            );
        }

        [Test]
        public void CompileRejectsDuplicatesAndFreezesAllConfiguration()
        {
            var module = new EndpointModule();
            var group = module.Root.MapGroup("/api");
            var endpoint = group
                .MapGet("/status", MinimalHandlers.StatusEndpoint)
                .WithName("status");
            module.Compile();
            Assert.DoesNotThrow(module.Compile);
            Assert.That(() => endpoint.AllowAnonymous(), Throws.InvalidOperationException);
            Assert.That(() => group.RequireAuthorization(), Throws.InvalidOperationException);
            Assert.That(
                () => module.ConfigureAuthorization(new AuthorizationOptions()),
                Throws.InvalidOperationException
            );
            Assert.That(module.Links.GetPath("status"), Is.EqualTo("/api/status"));
            var duplicate = new EndpointModule();
            duplicate.Root.MapGet("/{id}", MinimalHandlers.IdEndpoint);
            duplicate.Root.MapGet("/{other}", MinimalHandlers.StatusEndpoint);
            Assert.That(duplicate.Compile, Throws.InvalidOperationException);
            var missing = new EndpointModule();
            missing.Root.MapGet("/id", MinimalHandlers.IdEndpoint);
            Assert.That(missing.Compile, Throws.InvalidOperationException);
        }

        [Test]
        public void OpenApiUsesFlattenedBindingAndEffectivePolicies()
        {
            var module = new EndpointModule();
            var group = module.Root
                .MapGroup("/api")
                .RequireAuthorization();
            group
                .MapGet("/search", MinimalHandlers.SearchEndpoint)
                .WithName("search");
            group
                .MapGet("/hidden", MinimalHandlers.StatusEndpoint)
                .ExcludeFromDescription();
            var json = new OpenApiDocumentBuilder("Minimal", "1")
                .AddEndpoints(module)
                .Build(MinimalJson.Default.Options)
                .ToJson();
            var output = System.IO.Path.Combine(AppContext.BaseDirectory, "openapi");
            System.IO.Directory.CreateDirectory(output);
            System.IO.File.WriteAllText(System.IO.Path.Combine(output, "minimal-endpoints.json"), json);
            Assert.That(
                json,
                Does
                    .Contain("/api/search")
                    .And.Contain("search")
                    .And.Contain("Page")
            );
            Assert.That(json, Does.Not.Contain("/api/hidden"));
        }
    }

    public sealed class MinimalSearch
    {
        [System.ComponentModel.DataAnnotations.Range(1, 20)]
        public int Page { get; set; } = 1;
    }

    [GeneratedEndpointBinding(JsonSerializerContext = typeof(MinimalJson))]
    internal static partial class MinimalHandlers
    {
        public static OkResult<string> Read(
            string tenant,
            int id,
            [QueryObject, Validate] MinimalSearch input,
            [HeaderField("X-Label")] string label = "none",
            [CookieField("theme")] string theme = "none"
        ) =>
            HttpResults.Ok(
                tenant
                    + "|"
                    + id.ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + input.Page.ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + label
                    + "|"
                    + theme
            );

        public static OkResult<string> Search([QueryObject, Validate] MinimalSearch input) =>
            HttpResults.Ok(input.Page.ToString(CultureInfo.InvariantCulture));

        public static OkResult<string> Form([FormObject, Validate] MinimalSearch input) => Search(input);

        public static OkResult<string> Json([JsonData, Validate] MinimalSearch input) => Search(input);

        public static OkResult<string> Status() => HttpResults.Ok("ok");

        public static OkResult<string> Id(int id) => HttpResults.Ok(id.ToString(CultureInfo.InvariantCulture));

        public static Task<OkResult<string>> Context(IHttpContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(HttpResults.Ok(context.Request.HttpMethod));
        }
    }

    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(MinimalSearch))]
    internal partial class MinimalJson : JsonSerializerContext { }
}
