using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authorization;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;
using static EmbedIO.Tests.SecurityHeadersFixtures;

namespace EmbedIO.Tests
{
    public sealed partial class SecurityHeadersTest
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task InheritedPoliciesSnapshotAndAuthorizationPrecedence(bool generated, bool insensitive)
        {
            HeaderAuditController.Created = 0;
            await TestWebServer.UseAsync(
                server =>
                {
                    var module = new WebApiModule("/api", ResponseSerializer.None(false))
                    {
                        CaseInsensitiveRoutes = insensitive
                    };
                    var policies = new Dictionary<string, SecurityHeadersPolicy>
                    {
                        ["class"] = new("default-src https://class.example"),
                        ["action"] = new("default-src https://action.example")
                    };
                    module.ConfigureSecurityHeaders(policies);
                    policies["action"] = new SecurityHeadersPolicy("default-src *");

                    if (generated) module.RegisterController(HeaderAuditBindings.HeaderAuditController);

                    else module.RegisterController<HeaderAuditController>();

                    Assert.That(() => module.ConfigureSecurityHeaders(policies), Throws.InvalidOperationException);
                    server.WithModule(module);
                },
                async client =>
                {
                    const string stem = "/api/";
                    using var denied = await client.GetAsync(stem + (insensitive ? "DENIED" : "denied"));

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                        Assert.That(
                            Header(denied, "Content-Security-Policy"),
                            Is.EqualTo("default-src https://action.example")
                        );
                        Assert.That(HeaderAuditController.Created, Is.Zero);
                    }

                    using var inherited = await client.GetAsync(stem + (insensitive ? "CLASS" : "class"));
                    Assert.That(
                        Header(inherited, "Content-Security-Policy"),
                        Is.EqualTo("default-src https://class.example")
                    );
                    using var action = await client.GetAsync(stem + (insensitive ? "ACTION" : "action"));
                    Assert.That(
                        Header(action, "Content-Security-Policy"),
                        Is.EqualTo("default-src https://action.example")
                    );
                }
            );
        }

        [Test]
        public void UnknownReflectionPolicyFailsAtomically()
        {
            var module = new WebApiModule("/");
            Assert.That(() => module.RegisterController<HeaderAuditController>(), Throws.InvalidOperationException);
            Assert.That(module.ControllerCount, Is.Zero);
        }
    }

    [SecurityHeaders("class")]
    public abstract class HeaderAuditBase : WebApiController
    {
        [SecurityHeaders("action")]
        public virtual string Action() => "action";
    }

    public sealed class HeaderAuditController : HeaderAuditBase
    {
        internal static int Created;

        public HeaderAuditController() => Interlocked.Increment(ref Created);

        [Route(HttpVerbs.Get, "/class")]
        public string Class() => HttpContext.RequestedPath;

        [Route(HttpVerbs.Get, "/action")]
        public override string Action() => "action";

        [Route(HttpVerbs.Get, "/denied")]
        [Authorize]
        [SecurityHeaders("action")]
        public string Denied() => HttpContext.RequestedPath;
    }

    [GeneratedControllerBinding(typeof(HeaderAuditController))]
    internal static partial class HeaderAuditBindings { }
}
