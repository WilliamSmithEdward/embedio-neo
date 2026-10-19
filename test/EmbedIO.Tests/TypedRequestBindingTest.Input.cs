using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authorization;
using EmbedIO.Endpoints;
using EmbedIO.OpenApi;
using EmbedIO.PlatformTests;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TypedRequestBindingTest
    {
        private static readonly Func<string, NameValueCollection> ParseFields = (typeof(WebServer).Assembly
            .GetType("EmbedIO.WebApi.BoundedRequestFields")
            ?.GetMethod("Parse", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Missing bounded parser."
            )).CreateDelegate<Func<string, NameValueCollection>>();

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExplicitRouteStrongIdsAndInheritedPropertyOptionsHaveParity(bool generated) =>
            await WithController(generated, async client =>
            {
                using var assertions = Assert.EnterMultipleScope();
                Assert.That(await client.GetStringAsync("/api/route/book-42"), Is.EqualTo("42"));
                using var rejected = await client.GetAsync("/api/route/invalid");
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(await client.GetStringAsync("/api/route-default"), Is.EqualTo("7"));
                Assert.That(await client.GetStringAsync("/api/inherited?n=value"), Is.EqualTo("value"));
                using var missing = await client.GetAsync("/api/inherited");
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            });

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExplicitDtoDuplicatePolicyAndNullableCustomArraysAreConsistent(bool generated) =>
            await WithController(generated, async client =>
            {
                using var assertions = Assert.EnterMultipleScope();
                Assert.That(await client.GetStringAsync("/api/policies?first=1&FIRST=2&last=3&last=4&ids=book-1&ids=&ids=book-3"), Is.EqualTo("1|4|1,,3"));
                Assert.That(await client.GetStringAsync("/api/policies"), Is.EqualTo("default-first|default-last|"));
                using var invalid = await client.GetAsync("/api/policies?ids=bad");
                Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(await client.GetStringAsync("/api/query?tags=a%2Bb&tags=a+b&tags=%F0%9F%98%80"), Does.Contain("a+b,a b,😀"));
            });

        [TestCase(false, "page=%", 400)]
        [TestCase(true, "page=%", 400)]
        [TestCase(false, "page=%GG", 400)]
        [TestCase(true, "page=%GG", 400)]
        [TestCase(false, "tags=%FF", 400)]
        [TestCase(true, "tags=%FF", 400)]
        [TestCase(false, "tags=%C0%AF", 400)]
        [TestCase(true, "tags=%C0%AF", 400)]
        [TestCase(false, "page=2&page=3", 400)]
        [TestCase(true, "page=2&page=3", 400)]
        [TestCase(false, "page=2&", 400)]
        [TestCase(true, "page=2&", 400)]
        [TestCase(false, "page=4", 200)]
        [TestCase(true, "page=4", 200)]
        public async Task StrictFormParsingRejectsMalformedEscapesWithoutChangingLegacyParsing(
            bool generated,
            string body,
            int status
        ) => await WithController(generated, async client =>
            {
                using var assertions = Assert.EnterMultipleScope();
                using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
                using var response = await client.PostAsync("/api/form", content);
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
                Assert.That(await client.GetStringAsync("/api/legacy?value=1&value=2"), Is.EqualTo("2"));
            });

        [TestCase(false)]
        [TestCase(true)]
        public async Task FormsEnforceMediaTypeAndDecodedBodyCapBeforeActivationOfAction(bool generated)
        {
            var controller = new TypedBindingController();
            await WithController(
                generated,
                async client =>
                {
                    using var assertions = Assert.EnterMultipleScope();
                    using var wrong = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var unsupported = await client.PostAsync("/api/form", wrong);
                    Assert.That(unsupported.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
                    using var large = new StringContent(
                        "tags=" + new string('a', 262145),
                        Encoding.UTF8,
                        "application/x-www-form-urlencoded"
                    );
                    using var rejected = await client.PostAsync("/api/form", large);
                    Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                    Assert.That(controller.Calls, Is.Zero);
                    Assert.That(controller.Disposed, Is.EqualTo(2));
                },
                controller
            );
        }

        [TestCase("a=%")]
        [TestCase("a=%0")]
        [TestCase("a=%GG")]
        [TestCase("a=%80")]
        [TestCase("a=%C0%AF")]
        [TestCase("a=%ED%A0%80")]
        [TestCase("a=1&&b=2")]
        [TestCase("a=1&")]
        [TestCase("=1")]
        public void StrictDecodingHasBoundedAndUnambiguousFailure(string source) =>
            Assert.Throws<ProblemDetailsException>(() => ParseFields(source));

        [Test]
        public void FlatFieldNamesUseOrdinalCaseInsensitiveComparison()
        {
            var fields = ParseFields("name=one&na%C2%ADme=two&NAME=three");
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(fields, Has.Count.EqualTo(2));
            Assert.That(fields.GetValues("name"), Is.EqualTo(["one", "three"]));
            Assert.That(fields["na\u00adme"], Is.EqualTo("two"));
        }

        [Test]
        public void NewFlatParserKeepsBracketNamesLiteralAndBoundsBeforeCollectionGrowth()
        {
            var fields = ParseFields("a[0]=one&a[1]=two&escaped=a%2Bb&space=a+b");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fields["a"], Is.Null);
                Assert.That(fields["a[0]"], Is.EqualTo("one"));
                Assert.That(fields["escaped"], Is.EqualTo("a+b"));
                Assert.That(fields["space"], Is.EqualTo("a b"));
                Assert.That(() => fields.Add("x", "y"), Throws.TypeOf<NotSupportedException>());
            }

            Assert.Throws<ProblemDetailsException>(() => ParseFields(string.Join("&", Enumerable.Repeat("a=x", 1025))));
            Assert.Throws<ProblemDetailsException>(() => ParseFields("a=" + new string('x', 262145)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AuthorizationPrecedesControllerConstructionAndMalformedBinding(bool generated)
        {
            var constructed = 0;
            var module = new WebApiModule("/auth", ResponseSerializer.None(false));
            module.ConfigureAuthorization(
                new AuthorizationOptions().AddPolicy("deny", (_, _) => Task.FromResult(false))
            );

            if (generated) module.WithController(AuthBindingBindings.BindingAuthController, () =>
                {
                    constructed++;
                    return new BindingAuthController();
                });

            else module.WithController(() =>
                {
                    constructed++;
                    return new BindingAuthController();
                });

            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var assertions = Assert.EnterMultipleScope();
                using var response = await client.GetAsync("/auth/value?value=%FF");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            });
            Assert.That(constructed, Is.Zero);
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public async Task GeneratedAndReflectionBindingsWorkOnRealHttpConnections(bool generated, int protocol) =>
            await RequestBindingWire.RunAsync(generated, protocol, false);
    }

    internal static class RequestBindingWire
    {
        internal static async Task RunAsync(bool generated, int protocol, bool minimal)
        {
            if (protocol == 3 && (!QuicListener.IsSupported || !QuicConnection.IsSupported))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1")
                    Assert.Fail("QUIC is required by this job.");

                Assert.Ignore("The host does not provide QUIC.");
                return;
            }

            var url = TestObjects.Resources.GetServerAddress();

            if (protocol > 1) url = url.Replace("http://", "https://", StringComparison.Ordinal);

            using var certificate = protocol > 1 ? HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable) : null;
            using var stop = new CancellationTokenSource();
            using var server = (protocol == 1
                ? new WebServer(url)
                : new WebServer(
                    protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO,
                    certificate ?? throw new InvalidOperationException("Missing certificate."),
                    url
                )).WithProblemDetails(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null });

            if (minimal)
            {
                var module = new EndpointModule(ResponseSerializer.None(false));
                var group = module.Root.MapGroup("/api");

                if (generated)
                {
                    group.MapGet("/query", MinimalWireHandlers.QueryEndpoint);
                    group.MapPost("/form", MinimalWireHandlers.FormEndpoint);
                    group.MapGet("/cookie-last", MinimalWireHandlers.CookieEndpoint);
                }

                else
                {
                    group.MapGet("/query", MinimalWireHandlers.Query);
                    group.MapPost("/form", MinimalWireHandlers.Form);
                    group.MapGet("/cookie-last", MinimalWireHandlers.Cookie);
                }

                server.WithModule(module);
            }

            else
            {
                var module = new WebApiModule("/api", ResponseSerializer.None(false));

                if (generated) module.WithController(TypedBindings.TypedBindingController);

                else module.WithController<TypedBindingController>();

                server.WithModule(module);
            }

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) =>
            {
                if (change.NewState == WebServerState.Listening) ready.TrySetResult();
            };
            var running = server.RunAsync(stop.Token);
            using var transport = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, peer, _, _) => certificate != null
                        && peer?.GetCertHashString()
                        == certificate.GetCertHashString()
                }
            };

            try
            {
                using var client = new HttpClient(transport)
                {
                    BaseAddress = new Uri(url),
                    Timeout = TimeSpan.FromSeconds(10),
                    DefaultRequestVersion = protocol == 3
                        ? HttpVersion.Version30
                        : protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                using var assertions = Assert.EnterMultipleScope();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(await client.GetStringAsync("api/query?page=4&id=book-3"), Does.StartWith("4|25|"));
                Assert.That(await client.GetStringAsync("api/query?page=5&id=book-8"), Does.StartWith("5|25|"));

                for (var page = 2; page <= 3; page++)
                {
                    using var fresh = new StringContent(
                        "page=" + page.ToString(CultureInfo.InvariantCulture),
                        Encoding.UTF8,
                        "application/x-www-form-urlencoded"
                    );
                    using var freshResponse = await client.PostAsync("api/form", fresh);
                    Assert.That(
                        await freshResponse.Content.ReadAsStringAsync(),
                        Does.StartWith(page.ToString(CultureInfo.InvariantCulture) + "|25|")
                    );
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, "api/cookie-last")
                {
                    Version = client.DefaultRequestVersion,
                    VersionPolicy = client.DefaultVersionPolicy
                };
                request.Headers.TryAddWithoutValidation("Cookie", "theme=one; theme=two");
                using var cookie = await client.SendAsync(request);
                Assert.That(await cookie.Content.ReadAsStringAsync(), Is.EqualTo("two"));
                using var form = new StringContent("page=2&page=3", Encoding.UTF8, "application/x-www-form-urlencoded");
                using var rejected = await client.PostAsync("api/form", form);
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                using var large = new StringContent(
                    "tags=" + new string('x', 262145),
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                );
                using var bounded = await client.PostAsync("api/form", large);
                Assert.That(bounded.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(await client.GetStringAsync("api/query?page=7"), Does.StartWith("7|25|"));
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    public class BindingBase
    {
        [RequestField("n", Required = true)]
        public virtual string Name { get; set; } = "initial";
    }

    public sealed class BindingDerived : BindingBase
    {
        public override string Name { get; set; } = "derived";
    }

    public sealed class BindingPolicies
    {
        [RequestField("first", DuplicateValues = RequestDuplicateValues.First)]
        public string First { get; set; } = "default-first";
        [RequestField("last", DuplicateValues = RequestDuplicateValues.Last)]
        public string Last { get; set; } = "default-last";
        public BindingBookId?[] Ids { get; set; } = [];
    }

    public sealed partial class TypedBindingController
    {
        [Route(HttpVerbs.Get, "/route/{book}")]
        public string StrongRoute([RouteField("book")] BindingBookId id)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return id.Value.ToString(CultureInfo.InvariantCulture);
        }

        [Route(HttpVerbs.Get, "/route-default")]
        public string RouteDefault([RouteField("absent", Required = false)] int value = 7)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return value.ToString(CultureInfo.InvariantCulture);
        }

        [Route(HttpVerbs.Get, "/inherited")]
        public string Inherited([QueryObject] BindingDerived input)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return (input ?? throw new ArgumentNullException(nameof(input))).Name;
        }

        [Route(HttpVerbs.Get, "/policies")]
        public string Policies([QueryObject] BindingPolicies input)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(input);

            return input.First
                + "|"
                + input.Last
                + "|"
                + string.Join(",", input.Ids.Select(id => id?.Value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    [GeneratedControllerBinding(typeof(BindingAuthController))]
    internal static partial class AuthBindingBindings { }

    public sealed class BindingAuthController : WebApiController
    {
        [Authorize("deny")]
        [Route(HttpVerbs.Get, "/value")]
        public string Value([QueryObject] BindingRequired input)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return (input ?? throw new ArgumentNullException(nameof(input))).Value;
        }
    }
}
