using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Authorization;
using EmbedIO.Caching;
using EmbedIO.DependencyInjection;
using EmbedIO.Endpoints;
using EmbedIO.Problems;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class MinimalEndpointsTest
    {
        [TestCase("relative")]
        [TestCase("//evil.example")]
        [TestCase("/bad#fragment")]
        [TestCase("/bad\\path")]
        [TestCase("/bad/{")]
        public void InvalidPrefixesFailBeforeAnyEndpoint(string prefix)
        {
            var module = new EndpointModule();
            Assert.That(
                () => module.Root.MapGroup(prefix),
                Throws
                    .InstanceOf<ArgumentException>()
                    .Or.InstanceOf<FormatException>()
            );
            module.Root.MapGet("/ok", MinimalHandlers.StatusEndpoint);
            Assert.DoesNotThrow(module.Compile);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DirectDelegateAndValueTaskFrameworkBindings(bool generated) =>
            await TestWebServer.UseAsync(
                server =>
                {
                    if (generated)
                    {
                        server.MapGet("/context", MinimalHandlers.ContextEndpoint);
                        server.MapGet("/value", MinimalAdjacentHandlers.ValueEndpoint);
                    }

                    else
                    {
                        server.MapGet("/context", MinimalHandlers.Context);
                        server.MapGet("/value", MinimalAdjacentHandlers.Value);
                    }

                    server.MapGet("/lambda", () => HttpResults.Ok("lambda"));
                },
                async client =>
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(await client.GetStringAsync("/context"), Is.EqualTo("\"GET\""));
                        Assert.That(await client.GetStringAsync("/value"), Is.EqualTo("\"value\""));
                        Assert.That(await client.GetStringAsync("/lambda"), Is.EqualTo("\"lambda\""));
                    }
                }
            );

        private static readonly string[] ExpectedHookOrder = new[]
        {
            "root-before", "child-before", "endpoint-after", "child-after", "root-after"
        };

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompletionHooksAllRunAndPreserveBindingFailure(bool generated)
        {
            var calls = new List<string>();
            await TestWebServer.UseAsync(
                server =>
                {
                    server.WithProblemDetails();
                    var root = server.MapGroup("/api");
                    var child = root.MapGroup("/inner");
                    root.OnBeforeHandler(_ =>
                    {
                        calls.Add("root-before");
                        return Task.CompletedTask;
                    });
                    child.OnBeforeHandler(_ =>
                    {
                        calls.Add("child-before");
                        return Task.CompletedTask;
                    });
                    root.OnAfterHandler(_ =>
                    {
                        calls.Add("root-after");
                        return Task.CompletedTask;
                    });
                    child.OnAfterHandler(_ =>
                    {
                        calls.Add("child-after");
                        return Task.FromException(new InvalidOperationException("private completion fault"));
                    });
                    var handler = generated
                        ? MinimalHandlers.SearchEndpoint
                        : EndpointHandler.FromDelegate((Func<MinimalSearch, OkResult<string>>)MinimalHandlers.Search);
                    child
                        .MapGet("/search", handler)
                        .OnAfterHandler(_ =>
                        {
                            calls.Add("endpoint-after");
                            return Task.CompletedTask;
                        });
                },
                async client =>
                {
                    using var response = await client.GetAsync("/api/inner/search?Page=0");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                        Assert.That(
                            await response.Content.ReadAsStringAsync(),
                            Does.Not.Contain("private completion fault")
                        );
                        Assert.That(calls, Is.EqualTo(ExpectedHookOrder));
                    }
                }
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DependencyScopeLivesThroughSerializationAndIsReleasedPerRequest(bool generated)
        {
            var services = new List<MinimalService>();
            using var provider = new ServiceCollection()
                .AddScoped(_ =>
                {
                    var service = new MinimalService(services.Count + 1);
                    services.Add(service);
                    return service;
                })
                .BuildServiceProvider();
            await TestWebServer.UseAsync(
                server =>
                {
                    server.WithDependencyInjection(provider);

                    if (generated) server.MapGet("/service", MinimalAdjacentHandlers.ServiceEndpoint);

                    else server.MapGet("/service", MinimalAdjacentHandlers.Service);
                },
                async client =>
                {
                    Assert.That(await client.GetStringAsync("/service"), Is.EqualTo("\"1\""));

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(await client.GetStringAsync("/service"), Is.EqualTo("\"2\""));
                        Assert.That(services, Has.Count.EqualTo(2));
                    }

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(services[0].Disposed, Is.EqualTo(1));
                        Assert.That(services[1].Disposed, Is.EqualTo(1));
                    }
                }
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OutputCacheIsOptInAndEndpointCanDisableInheritedPolicy(bool generated)
        {
            MinimalAdjacentHandlers.Count = 0;
            var cache = new OutputCache();
            await TestWebServer.UseAsync(
                server =>
                {
                    var root = server.MapGroup("/api");
                    root.Module.ConfigureOutputCaching(
                        cache,
                        new Dictionary<string, OutputCachePolicy> { ["short"] = new(TimeSpan.FromMinutes(1)) }
                    );
                    root.CacheOutput("short");
                    var handler = generated
                        ? MinimalAdjacentHandlers.CountedEndpoint
                        : EndpointHandler.FromDelegate((Func<OkResult<string>>)MinimalAdjacentHandlers.Counted);
                    root.MapGet("/cached", handler);
                    root
                        .MapGet("/live", handler)
                        .DisableOutputCache();
                },
                async client =>
                {
                    Assert.That(await client.GetStringAsync("/api/cached"), Is.EqualTo("\"1\""));

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(await client.GetStringAsync("/api/cached"), Is.EqualTo("\"1\""));
                        Assert.That(await client.GetStringAsync("/api/live"), Is.EqualTo("\"2\""));
                    }

                    Assert.That(await client.GetStringAsync("/api/live"), Is.EqualTo("\"3\""));
                }
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task GroupRequirementsCombineAndAnonymousEndpointCanRequireAgain(bool generated) =>
            await TestWebServer.UseAsync(
                server =>
                {
                    server.WithModule(new PreRequestModule(context =>
                        {
                            context.GetImplementation().User = new GenericPrincipal(new GenericIdentity("reader", "test"), ["reader"]);
                            return Task.CompletedTask;
                        }));
                    var root = server
                        .MapGroup("/api")
                        .RequireAuthorization(roles: ["reader"]);
                    root.Module.ConfigureAuthorization(
                        new AuthorizationOptions().AddPolicy("deny", (_, _) => Task.FromResult(false))
                    );
                    var nested = root
                        .MapGroup("/nested")
                        .RequireAuthorization("deny");
                    var handler = generated
                        ? MinimalHandlers.StatusEndpoint
                        : EndpointHandler.FromDelegate((Func<OkResult<string>>)MinimalHandlers.Status);
                    nested.MapGet("/denied", handler);
                    nested
                        .MapGet("/anonymous", handler)
                        .AllowAnonymous();
                    nested
                        .MapGet("/required", handler)
                        .AllowAnonymous()
                        .RequireAuthorization();
                },
                async client =>
                {
                    using var denied = await client.GetAsync("/api/nested/denied");
                    Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                    using var anonymous = await client.GetAsync("/api/nested/anonymous");
                    Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var required = await client.GetAsync("/api/nested/required");
                    Assert.That(required.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                }
            );

        [Test]
        public async Task LiteralRoutePolicyCannotBeShadowedByParameterRoute() =>
            await TestWebServer.UseAsync(
                server =>
                {
                    var group = server.MapGroup("/api");
                    group
                        .MapGet("/{value}", MinimalHandlers.StatusEndpoint)
                        .AllowAnonymous();
                    group
                        .MapGet("/admin", MinimalHandlers.StatusEndpoint)
                        .RequireAuthorization();
                },
                async client =>
                {
                    using var admin = await client.GetAsync("/api/admin");
                    Assert.That(admin.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    using var other = await client.GetAsync("/api/ordinary");
                    Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                }
            );

        private static readonly string[] AllowedMethods = new[] { "GET", "POST" };

        [Test]
        public async Task MultipleMethodsShareSettingsAndAdvertiseAllow() =>
            await TestWebServer.UseAsync(
                server =>
                {
                    server
                        .MapMethods("/multi", MinimalHandlers.StatusEndpoint, HttpVerbs.Get, HttpVerbs.Post)
                        .WithName("multi");
                },
                async client =>
                {
                    using var get = await client.GetAsync("/multi");
                    using var post = await client.PostAsync("/multi", null);
                    using var denied = await client.DeleteAsync("/multi");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
                        Assert.That(denied.Content.Headers.Allow, Is.EquivalentTo(AllowedMethods));
                    }
                }
            );

        [Test]
        public void MultipleMethodMetadataHasDistinctOperationsAndOneNamedLink()
        {
            var module = new EndpointModule();
            module.Root
                .MapMethods("/multi", MinimalHandlers.StatusEndpoint, HttpVerbs.Get, HttpVerbs.Post)
                .WithName("multi");
            var document = new EmbedIO.OpenApi.OpenApiDocumentBuilder("Multi", "1")
                .AddEndpoints(module)
                .Build(MinimalJson.Default.Options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    document.Root["paths"]?["/multi"]?["get"]?["operationId"]?.GetValue<string>(),
                    Is.EqualTo("multi_GET")
                );
                Assert.That(
                    document.Root["paths"]?["/multi"]?["post"]?["operationId"]?.GetValue<string>(),
                    Is.EqualTo("multi_POST")
                );
                Assert.That(module.Links.GetPath("multi"), Is.EqualTo("/multi"));
            }
        }

        [Test]
        public async Task CaseInsensitiveRoutesAreExplicitAndRejectConflictingAliases()
        {
            await TestWebServer.UseAsync(
                server =>
                {
                    var module = server.GetEndpointModule();
                    module.CaseInsensitiveRoutes = true;
                    module.Root.MapGet("/Status", MinimalHandlers.StatusEndpoint);
                    Assert.That(module.EndpointCount, Is.EqualTo(1));
                },
                async client => Assert.That(await client.GetStringAsync("/STATUS"), Is.EqualTo("\"ok\""))
            );
            var invalid = new EndpointModule { CaseInsensitiveRoutes = true };
            invalid.Root.MapGet("/Status", MinimalHandlers.StatusEndpoint);
            invalid.Root.MapGet("/status", MinimalHandlers.StatusEndpoint);
            Assert.That(invalid.Compile, Throws.InvalidOperationException);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task IdempotentWritesReplayWithoutRepeatedHandler(bool generated)
        {
            MinimalAdjacentHandlers.Count = 0;
            var coordinator = new EmbedIO.Idempotency.IdempotencyCoordinator();
            await TestWebServer.UseAsync(
                server =>
                {
                    var group = server.MapGroup("/write");
                    group.Module.ConfigureIdempotency(
                        coordinator,
                        new Dictionary<string, EmbedIO.Idempotency.IdempotencyPolicy>
                        {
                            ["write"] = new(
                                "minimal-test",
                                _ => "trusted-test-identity",
                                TimeSpan.FromMinutes(1),
                                4096,
                                4096
                            )
                        }
                    );
                    var handler = generated
                        ? MinimalAdjacentHandlers.CountedEndpoint
                        : EndpointHandler.FromDelegate((Func<OkResult<string>>)MinimalAdjacentHandlers.Counted);
                    group
                        .MapPost("/", handler)
                        .WithIdempotency("write");
                },
                async client =>
                {
                    async Task<HttpResponseMessage> Send(string body)
                    {
                        using var message = new HttpRequestMessage(HttpMethod.Post, "/write")
                        {
                            Content = new StringContent(body)
                        };
                        message.Headers.Add("Idempotency-Key", "one");
                        return await client.SendAsync(message);
                    }

                    using var first = await Send("first");
                    using var replay = await Send("first");
                    using var conflict = await Send("different");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("\"1\""));
                        Assert.That(await replay.Content.ReadAsStringAsync(), Is.EqualTo("\"1\""));
                        Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                        Assert.That(MinimalAdjacentHandlers.Count, Is.EqualTo(1));
                    }
                }
            );
        }

        private static readonly string[] ExpectedSecurityHeaders = new[] { "default-src 'self'" };

        [TestCase(false)]
        [TestCase(true)]
        public async Task SecurityHeaderSelectionOverridesGroupsOnCacheHits(bool generated)
        {
            MinimalAdjacentHandlers.Count = 0;
            var cache = new OutputCache();
            await TestWebServer.UseAsync(
                server =>
                {
                    var group = server.MapGroup("/api");
                    group.Module.ConfigureSecurityHeaders(new Dictionary<string, EmbedIO.Security.SecurityHeadersPolicy>
                    {
                        ["outer"] = new("default-src 'none'"),
                        ["inner"] = new("default-src 'self'")
                    });
                    group.Module.ConfigureOutputCaching(
                        cache,
                        new Dictionary<string, OutputCachePolicy> { ["short"] = new(TimeSpan.FromMinutes(1)) }
                    );
                    group
                        .WithSecurityHeaders("outer")
                        .CacheOutput("short");
                    var handler = generated
                        ? MinimalAdjacentHandlers.CountedEndpoint
                        : EndpointHandler.FromDelegate((Func<OkResult<string>>)MinimalAdjacentHandlers.Counted);
                    group
                        .MapGet("/value", handler)
                        .WithSecurityHeaders("inner");
                },
                async client =>
                {
                    using var first = await client.GetAsync("/api/value");
                    using var replay = await client.GetAsync("/api/value");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(
                            first.Headers.GetValues("Content-Security-Policy"),
                            Is.EqualTo(ExpectedSecurityHeaders)
                        );
                        Assert.That(
                            replay.Headers.GetValues("Content-Security-Policy"),
                            Is.EqualTo(ExpectedSecurityHeaders)
                        );
                        Assert.That(MinimalAdjacentHandlers.Count, Is.EqualTo(1));
                    }
                }
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task BeforeHookCanAnswerWithoutBindingAndStillCompletes(bool generated)
        {
            var completed = 0;
            await TestWebServer.UseAsync(
                server =>
                {
                    var handler = generated
                        ? MinimalHandlers.SearchEndpoint
                        : EndpointHandler.FromDelegate((Func<MinimalSearch, OkResult<string>>)MinimalHandlers.Search);
                    server
                        .MapGet("/short", handler)
                        .OnBeforeHandler(async context =>
                        {
                            await context.SendStringAsync("short", "text/plain", System.Text.Encoding.UTF8);
                            context.SetHandled();
                        })
                        .OnAfterHandler(_ =>
                        {
                            completed++;
                            return Task.CompletedTask;
                        });
                },
                async client => Assert.That(await client.GetStringAsync("/short?Page=bad"), Is.EqualTo("short"))
            );
            Assert.That(completed, Is.EqualTo(1));
        }

        [Test]
        public async Task ServerExtensionsReuseAnExplicitModuleAndSerializer()
        {
            var module = new EndpointModule(ResponseSerializer.None(false));
            await TestWebServer.UseAsync(
                server =>
                {
                    server.WithModule(module);
                    Assert.That(server.GetEndpointModule(), Is.SameAs(module));
                    server.MapGet("/value", MinimalAdjacentHandlers.ValueEndpoint);
                },
                async client => Assert.That(await client.GetStringAsync("/value"), Is.EqualTo("value"))
            );
        }

        [Test]
        public void FluentOpenApiOverridesAreCopiedAndSupportStatusEnums()
        {
            var module = new EndpointModule();
            var tags = new[] { "books" };
            var group = module.Root.WithTags(tags);
            tags[0] = "mutated";
            group
                .MapGet("/text", MinimalHandlers.StatusEndpoint)
                .WithName("text")
                .WithSummary("Summary")
                .WithDescription("Description")
                .Produces<string>(HttpStatusCode.OK)
                .Produces(HttpStatusCode.NotFound);
            var document = new EmbedIO.OpenApi.OpenApiDocumentBuilder("Fluent", "1")
                .AddEndpoints(module)
                .Build(MinimalJson.Default.Options);
            var operation = document.Root["paths"]?["/text"]?["get"];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(operation?["summary"]?.GetValue<string>(), Is.EqualTo("Summary"));
                Assert.That(operation?["tags"]?[0]?.GetValue<string>(), Is.EqualTo("books"));
                Assert.That(operation?["responses"]?["404"], Is.Not.Null);
            }
        }

        [Test]
        public async Task InheritedInstanceHandlerPoliciesAreEnforced() =>
            await TestWebServer.UseAsync(
                server => server.MapGet("/inherited", new MinimalProtectedHandlers().Read),
                async client =>
                {
                    using var response = await client.GetAsync("/inherited");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                }
            );

        [TestCase(HttpVerbs.Get)]
        [TestCase(HttpVerbs.Post)]
        [TestCase(HttpVerbs.Put)]
        [TestCase(HttpVerbs.Patch)]
        [TestCase(HttpVerbs.Delete)]
        [TestCase(HttpVerbs.Options)]
        public async Task ExactMethodSelection(HttpVerbs verb) =>
            await TestWebServer.UseAsync(
                server => server
                    .GetEndpointModule()
                    .Root.MapMethods("/status", verb, MinimalHandlers.StatusEndpoint),
                async client =>
                {
                    using var request = new HttpRequestMessage(
                        new HttpMethod(
                            verb
                                .ToString()
                                .ToUpperInvariant()
                        ),
                        "/status"
                    );
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                }
            );
    }

    internal sealed class MinimalService(int id) : IDisposable
    {
        internal readonly int Id = id;
        internal int Disposed;

        public void Dispose() => Disposed++;
    }

    [GeneratedEndpointBinding]
    internal static partial class MinimalAdjacentHandlers
    {
        internal static int Count;

        public static ValueTask<string> Value() => ValueTask.FromResult("value");

        public static OkResult<string> Counted() =>
            HttpResults.Ok(
                Interlocked
                    .Increment(ref Count)
                    .ToString(CultureInfo.InvariantCulture)
            );

        public static OkResult<string> Service([FromServices] MinimalService service, IHttpContext context)
        {
            if (
                service.Disposed != 0
                || !ReferenceEquals(
                    service,
                    context
                        .GetRequestServices()
                        .GetRequiredService<MinimalService>()
                )
            )
                throw new InvalidOperationException("Wrong request scope.");

            return HttpResults.Ok(service.Id.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal class MinimalHandlerBase
    {
        private readonly string _payload = "protected";

        public OkResult<string> Read() => HttpResults.Ok(_payload);
    }

    [Authorize]
    internal sealed class MinimalProtectedHandlers : MinimalHandlerBase { }
}
