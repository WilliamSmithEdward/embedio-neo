using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Authorization;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.Tests.Issues;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class AuthorizationBindingTest
    {
        [TestCase(false, null, HttpStatusCode.Unauthorized)]
        [TestCase(true, null, HttpStatusCode.Unauthorized)]
        [TestCase(false, "reader", HttpStatusCode.Forbidden)]
        [TestCase(true, "reader", HttpStatusCode.Forbidden)]
        [TestCase(false, "writer", HttpStatusCode.Forbidden)]
        [TestCase(true, "writer", HttpStatusCode.Forbidden)]
        [TestCase(false, "wrong-policy", HttpStatusCode.Forbidden)]
        [TestCase(true, "wrong-policy", HttpStatusCode.Forbidden)]
        [TestCase(false, "all", HttpStatusCode.OK)]
        [TestCase(true, "all", HttpStatusCode.OK)]
        public async Task AuthorizationPrecedesActivationAndBinding(bool generated, string? token, HttpStatusCode expected)
        {
            AuthorizationUploadController.Created = 0;
            CountedAuthorizationBodyAttribute.Reads = 0;
            var module = Module(generated);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(Bearer(), module);
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Url + "api/secure") { Content = new StringContent("payload") };
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(expected));
            var accepted = expected == HttpStatusCode.OK ? 1 : 0;
            Assert.That(AuthorizationUploadController.Created, Is.EqualTo(accepted));
            Assert.That(CountedAuthorizationBodyAttribute.Reads, Is.EqualTo(accepted));
            if (accepted != 0) Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("payload"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InheritedAllowAnonymousOverridesControllerAndMethodRequirements(bool generated)
        {
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(Bearer(), Module(generated));
            using var client = new HttpClient();
            using var content = new StringContent("public");
            using var response = await client.PostAsync(server.Url + "api/public", content);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("public"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InheritedControllerAnonymousOverrideIsPreserved(bool generated)
        {
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated) module.RegisterController(AuthorizationBindings.AnonymousAuthorizationController);
            else module.RegisterController<AnonymousAuthorizationController>();
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient();
            Assert.That(await client.GetStringAsync(server.Url + "api/value"), Is.EqualTo("anonymous"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnknownPolicyFailsRegistrationWithoutAddingAController(bool generated)
        {
            var module = new WebApiModule("/api");
            Assert.That(() =>
            {
                if (generated) module.RegisterController(AuthorizationBindings.UnknownAuthorizationController);
                else module.RegisterController<UnknownAuthorizationController>();
            }, Throws.ArgumentException);
            Assert.That(module.ControllerCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task UnconfiguredAuthorizeStillDeniesAnonymousAndDoesNotAcquireController(bool generated)
        {
            var acquisitions = 0;
            var releases = 0;
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated)
                module.RegisterControllerWithContext(AuthorizationBindings.BasicAuthorizationController,
                    _ => { acquisitions++; return new BasicAuthorizationController(); }, (_, _) => { releases++; released.TrySetResult(); return Task.CompletedTask; });
            else
                module.RegisterControllerWithContext(typeof(BasicAuthorizationController),
                    _ => { acquisitions++; return new BasicAuthorizationController(); }, (_, _) => { releases++; released.TrySetResult(); return Task.CompletedTask; });
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(Bearer(), module);
            using var client = new HttpClient();
            using var denied = await client.GetAsync(server.Url + "api/value");
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(acquisitions, Is.Zero);
            Assert.That(releases, Is.Zero);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "all");
            Assert.That(await client.GetStringAsync(server.Url + "api/value"), Is.EqualTo("ok"));
            await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(acquisitions, Is.EqualTo(1));
            Assert.That(releases, Is.EqualTo(1));
        }

        [Test]
        public void OptionsAreConfiguredBeforeControllersAndAreSnapshotted()
        {
            var module = new WebApiModule("/api");
            var options = new AuthorizationOptions();
            module.ConfigureAuthorization(options);
            options.AddPolicy("missing", (_, _) => Task.FromResult(true));
            Assert.That(() => module.RegisterController(AuthorizationBindings.UnknownAuthorizationController), Throws.ArgumentException);
            module.RegisterController(AuthorizationBindings.BasicAuthorizationController);
            Assert.That(() => module.ConfigureAuthorization(options), Throws.InvalidOperationException);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OpenApiSecurityMatchesOptionalAndRequiredBearer(bool requireAuthentication)
        {
            var bearer = Bearer();
            bearer.RequireAuthentication = requireAuthentication;
            var document = new OpenApiDocumentBuilder("Authorized", "1")
                .AddController("/api", AuthorizationBindings.AuthorizationDocumentController)
                .AddBearerAuthentication("bearer", bearer)
                .Build(Serialization.Json.CreateOptions());
            var secure = document.Root["paths"]?["/api/secure"]?["post"];
            var anonymous = document.Root["paths"]?["/api/public"]?["post"];
            Assert.That(secure?["security"]?[0]?["bearer"]?.AsArray(), Is.Empty);
            Assert.That(secure?["responses"]?["401"], Is.Not.Null);
            Assert.That(secure?["responses"]?["403"], Is.Not.Null);
            Assert.That(secure?["x-embedio-authorization"]?["requirements"]?.AsArray(), Has.Count.EqualTo(2));
            Assert.That(anonymous?["security"] != null, Is.EqualTo(requireAuthentication));
            Assert.That(anonymous?["responses"]?["403"], Is.Null);
            Assert.That(anonymous?["responses"]?["401"], Is.Not.Null);
            Assert.That(document.Root["components"]?["securitySchemes"]?["bearer"]?["scheme"]?.GetValue<string>(), Is.EqualTo("bearer"));
        }

        [Test]
        public void ManualBindingCannotContradictOperationAuthorization()
        {
            var route = AuthorizationBindings.AuthorizationDocumentController.Routes[0];
            Assert.That(() => new WebApiRouteBinding<AuthorizationDocumentController>(route.Verb, route.Route,
                route.IsBaseRoute, route.HandlerId, route.CreateInvoker, route.Operation, null), Throws.ArgumentException);
        }

        [Test]
        public void MetadataCopiesCallerArrays()
        {
            var roles = new[] { "reader" };
            var requirement = new AuthorizationRequirement(null, roles);
            var requirements = new[] { requirement };
            var metadata = new AuthorizationMetadata(false, requirements);
            roles[0] = "admin";
            requirements[0] = new AuthorizationRequirement("other", Array.Empty<string>());
            Assert.That(metadata.Requirements[0], Is.SameAs(requirement));
            Assert.That(requirement.Roles, Is.EqualTo(new[] { "reader" }));
        }

        [Test]
        public void GeneratedMetadataIncludesControllerAndOverriddenMethodAttributes()
        {
            var route = AuthorizationBindings.AuthorizationUploadController.Routes.Single(item => item.Route == "/secure");
            var metadata = route.Authorization ?? throw new AssertionException("Missing authorization metadata.");
            Assert.That(metadata, Is.SameAs(route.Operation?.Authorization));
            Assert.That(metadata.Requirements, Has.Count.EqualTo(5));
            Assert.That(metadata.Requirements.Select(item => item.Policy).Where(item => item != null),
                Is.EquivalentTo(new[] { "class-policy", "tenant", "second-policy" }));
        }

        private static WebApiModule Module(bool generated)
        {
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            module.ConfigureAuthorization(new AuthorizationOptions()
                .AddPolicy("class-policy", (_, _) => Task.FromResult(true))
                .AddPolicy("tenant", (context, _) => Task.FromResult(context.User.Identity?.Name != "wrong-policy"))
                .AddPolicy("second-policy", (_, _) => Task.FromResult(true)));
            if (generated) module.RegisterController(AuthorizationBindings.AuthorizationUploadController);
            else module.RegisterController<AuthorizationUploadController>();
            return module;
        }

        private static BearerAuthenticationModule Bearer() => new("/api", (token, _) =>
        {
            var roles = token == "reader" ? new[] { "reader" } : token == "writer" ? new[] { "writer" } : new[] { "reader", "writer" };
            var identity = new ClaimsIdentity(roles.Select(role => new Claim(ClaimTypes.Role, role)).Append(new Claim(ClaimTypes.Name, token)), "test");
            return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
        });
    }

    [GeneratedControllerBinding(typeof(AuthorizationUploadController))]
    [GeneratedControllerBinding(typeof(UnknownAuthorizationController))]
    [GeneratedControllerBinding(typeof(BasicAuthorizationController))]
    [GeneratedControllerBinding(typeof(AuthorizationDocumentController))]
    [GeneratedControllerBinding(typeof(AnonymousAuthorizationController))]
    internal static partial class AuthorizationBindings { }

    [Authorize(Roles = "reader, admin")]
    public abstract class AuthorizationBaseController : WebApiController
    {
        [Route(HttpVerbs.Post, "/secure")]
        [Authorize("tenant")]
        public virtual string Secure([CountedAuthorizationBody] string body) => body;
        [Route(HttpVerbs.Post, "/public")]
        [AllowAnonymous]
        public virtual string Public([CountedAuthorizationBody] string body) => body;
    }

    [Authorize("class-policy")]
    public sealed class AuthorizationUploadController : AuthorizationBaseController
    {
        public static int Created;
        public AuthorizationUploadController() => Interlocked.Increment(ref Created);
        [Authorize(Roles = "writer")]
        [Authorize("second-policy")]
        public override string Secure(string body) => body;
        [Authorize("tenant")]
        public override string Public(string body) => body;
    }

    [Authorize("missing")]
    public sealed class UnknownAuthorizationController : WebApiController
    {
        [Route(HttpVerbs.Get, "/value")]
        public string Value() => "ok";
    }

    [Authorize]
    public sealed class BasicAuthorizationController : WebApiController
    {
        [Route(HttpVerbs.Get, "/value")]
        public string Value() => "ok";
    }

    [Authorize("tenant")]
    [Authorize(Roles = "reader")]
    public sealed class AuthorizationDocumentController : WebApiController
    {
        [Route(HttpVerbs.Post, "/secure")]
        public string Secure() => "ok";
        [Route(HttpVerbs.Post, "/public")]
        [AllowAnonymous]
        public string Public() => "ok";
    }

    [AllowAnonymous]
    public abstract class AnonymousAuthorizationBase : WebApiController { }

    [Authorize]
    public sealed class AnonymousAuthorizationController : AnonymousAuthorizationBase
    {
        [Authorize(Roles = "admin")]
        [Route(HttpVerbs.Get, "/value")]
        public string Value() => "anonymous";
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = true)]
    public sealed class CountedAuthorizationBodyAttribute : Attribute, IRequestDataAttribute<WebApiController, string>
    {
        public static int Reads;
        public async Task<string?> GetRequestDataAsync(WebApiController controller, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(controller);
            Interlocked.Increment(ref Reads);
            return await controller.HttpContext.GetRequestBodyAsStringAsync().ConfigureAwait(false);
        }
    }
}
