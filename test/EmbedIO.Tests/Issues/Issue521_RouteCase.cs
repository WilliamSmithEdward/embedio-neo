using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using EmbedIO.Authentication;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue521_RouteCase
    {
        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task LiteralCaseIsOptionalAndDataAndMountStayUnchanged(HttpListenerMode mode, bool enabled)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            var api = new WebApiModule("/api") { CaseInsensitiveRoutes = enabled };
            api.RegisterController<DataController>();
            var strict = new WebApiModule("/strict");
            strict.RegisterController<DataController>();
            server.WithModule(api).WithModule(strict);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var running = server.RunAsync(stop.Token);
            try
            {
                async Task Check(string path, HttpStatusCode expected, string? value = null)
                {
                    using var response = await client.GetAsync(url + path);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                    if (value != null)
                        Assert.That(EmbedIO.Serialization.Json.Deserialize<string>(await response.Content.ReadAsStringAsync()), Is.EqualTo(value));
                }
                await Check("api/GetTest/AbC?tag=XyZ", HttpStatusCode.OK, "AbC|XyZ|/GetTest/AbC|/api/GetTest/AbC?tag=XyZ");
                await Check("api/gettest/AbC?tag=XyZ", enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound,
                    enabled ? "AbC|XyZ|/gettest/AbC|/api/gettest/AbC?tag=XyZ" : null);
                await Check("api/GETTEST", enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound,
                    enabled ? "||/GETTEST|/api/GETTEST" : null);
                await Check("API/GetTest/AbC", HttpStatusCode.NotFound);
                await Check("strict/gettest/AbC", HttpStatusCode.NotFound);
                await Check("strict/GetTest/AbC", HttpStatusCode.OK, "AbC||/GetTest/AbC|/strict/GetTest/AbC");
                await Check("api/a.b+/MiXeD", enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, enabled ? "MiXeD" : null);
                await Check("api/axb/MiXeD", HttpStatusCode.NotFound);
                await Check("api/items/AbC/child", enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, enabled ? "AbC|/child" : null);
                await Check("api/loose/subpath", enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound, enabled ? "/subpath" : null);
                using var invalidVerb = await client.PostAsync(url + "api/gettest/AbC", new ByteArrayContent(Array.Empty<byte>()));
                Assert.That(invalidVerb.StatusCode, Is.EqualTo(enabled ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.NotFound));
                Assert.Throws<InvalidOperationException>(() => api.CaseInsensitiveRoutes = enabled);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase("en-US")]
        [TestCase("tr-TR")]
        public async Task InvariantLiteralMatchingPreservesUnicodeValues(string culture)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var url = Resources.GetServerAddress();
                using var stop = new CancellationTokenSource();
                using var server = new WebServer(url);
                var module = new WebApiModule("/api") { CaseInsensitiveRoutes = true };
                module.RegisterController<DataController>();
                server.WithModule(module);
                var running = server.RunAsync(stop.Token);
                try
                {
                    using var client = new HttpClient();
                    using var response = await client.GetAsync(url + "api/file/İIıi");
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(EmbedIO.Serialization.Json.Deserialize<string>(await response.Content.ReadAsStringAsync()), Is.EqualTo("İIıi"));
                }
                finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Test]
        public void PublicParserAndAttributesKeepTheirCachedSensitiveMatchers()
        {
            var before = RouteMatcher.Parse("/GetTest/{id?}", false);
            var module = new WebApiModule("/api") { CaseInsensitiveRoutes = true };
            module.RegisterController<DataController>();
            var after = RouteMatcher.Parse("/GetTest/{id?}", false);
            Assert.That(after, Is.SameAs(before));
            Assert.That(after.Match("/gettest/AbC"), Is.Null);
            Assert.That(((after).Match("/GetTest/AbC") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))["id"], Is.EqualTo("AbC"));
            Assert.That(new WebApiModule("/").CaseInsensitiveRoutes, Is.False);
        }

        [Test]
        public void PolicyChangesAfterRegistrationAreRejected()
        {
            var module = new WebApiModule("/");
            module.RegisterController<DataController>();
            Assert.Throws<InvalidOperationException>(() => module.CaseInsensitiveRoutes = true);
            module.CaseInsensitiveRoutes = false;
        }

        [TestCase(typeof(CollisionController))]
        [TestCase(typeof(AnyCollisionController))]
        public void CaseOnlyConflictsFailBeforeRegisteringAnyHandler(Type type)
        {
            var module = new WebApiModule("/") { CaseInsensitiveRoutes = true };
            Assert.Throws<ArgumentException>(() => module.RegisterController(type));
            Assert.That(module.ControllerCount, Is.Zero);
            module.CaseInsensitiveRoutes = false;
            module.RegisterController(type);
            Assert.That(module.ControllerCount, Is.EqualTo(1));
        }

        [Test]
        public void InheritedMethodsWithDifferentFactoriesAreNotSilentlyDeduplicated()
        {
            var module = new WebApiModule("/") { CaseInsensitiveRoutes = true };
            module.RegisterController<InheritedA>();
            Assert.Throws<ArgumentException>(() => module.RegisterController<InheritedB>());
            Assert.That(module.ControllerCount, Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task SameHandlerAliasesDeduplicateAndDifferentVerbsRemainUsable(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            var module = new WebApiModule("/") { CaseInsensitiveRoutes = true };
            module.RegisterController<AliasController>();
            server.WithModule(module);
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                using var get = await client.GetAsync(url + "vAlUe/AbC");
                using var post = await client.PostAsync(url + "VaLuE/AbC", new ByteArrayContent(Array.Empty<byte>()));
                Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(EmbedIO.Serialization.Json.Deserialize<string>(await get.Content.ReadAsStringAsync()), Is.EqualTo("get:AbC"));
                Assert.That(EmbedIO.Serialization.Json.Deserialize<string>(await post.Content.ReadAsStringAsync()), Is.EqualTo("post:AbC"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task OriginalOptionalIntegerRouteWorksWithUnchangedBinding(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            var module = new WebApiModule("/api") { CaseInsensitiveRoutes = true };
            module.RegisterController<IntegerController>();
            server.WithModule(module);
            server.WithWebApi("/strict", api => api.WithController<IntegerController>());
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                Assert.That(EmbedIO.Serialization.Json.Deserialize<string>(await client.GetStringAsync(url + "api/gettest/1")), Is.EqualTo("GetTest success [id=1]!"));
                Assert.That(EmbedIO.Serialization.Json.Deserialize<string>(await client.GetStringAsync(url + "api/GETTEST")), Is.EqualTo("GetTest success [id=]!"));
                using var invalid = await client.GetAsync(url + "api/gettest/NotAnInteger");
                using var baseline = await client.GetAsync(url + "strict/GetTest/NotAnInteger");
                Assert.That(invalid.StatusCode, Is.EqualTo(baseline.StatusCode));
                Assert.That(invalid.IsSuccessStatusCode, Is.False);
                TestContext.Out.WriteLine("Unchanged invalid nullable integer status: " + (int)baseline.StatusCode);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task BodyAndQueryBindingAndBaseScopedAuthenticationArePreserved(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.WithModule(new BasicAuthenticationModule("/api").WithAccount("user", "password"));
            var module = new WebApiModule("/api") { CaseInsensitiveRoutes = true };
            module.RegisterController<BodyController>();
            server.WithModule(module);
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                using var unauthorized = await client.PostAsync(url + "api/value/AbC?tag=XyZ", new StringContent("{\"Value\":\"MiXeD\"}", Encoding.UTF8, "application/json"));
                Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                using var request = new HttpRequestMessage(HttpMethod.Post, url + "api/vAlUe/AbC?tag=XyZ");
                request.Headers.TryAddWithoutValidation("Authorization", "Basic dXNlcjpwYXNzd29yZA==");
                request.Content = new StringContent("{\"Value\":\"MiXeD\"}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(EmbedIO.Serialization.Json.Deserialize<string>(await response.Content.ReadAsStringAsync()), Is.EqualTo("AbC|XyZ|MiXeD"));
                using var wrongMount = await client.PostAsync(url + "API/value/AbC", new ByteArrayContent(Array.Empty<byte>()));
                Assert.That(wrongMount.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [Test]
        public void CaseOnlyAliasesCannotSilentlyRenameCapturedParameters()
        {
            var module = new WebApiModule("/") { CaseInsensitiveRoutes = true };
            Assert.Throws<ArgumentException>(() => module.RegisterController<RenamedAliasController>());
            Assert.That(module.ControllerCount, Is.Zero);
        }

        public sealed class DataController : WebApiController
        {
            [Route(HttpVerbs.Get, "/GetTest/{id?}")]
            public string Get(string? id) => id + "|" + Request.QueryString["tag"] + "|" + Route.Path + "|" + Request.RawTarget;
            [Route(HttpVerbs.Get, "/A.B+/{value}")]
            [Route(HttpVerbs.Get, "/FILE/{value}")]
            public string Value(string value) => value;
            [BaseRoute(HttpVerbs.Get, "/Items/{value}")]
            public string Items(string value) => value + "|" + Route.SubPath;
            [BaseRoute(HttpVerbs.Get, "/Loose")]
            public string Loose() => (Route.SubPath ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        }
        public sealed class CollisionController : WebApiController
        {
            [Route(HttpVerbs.Get, "/Value")] public string A() => "a";
            [Route(HttpVerbs.Get, "/value")] public string B() => "b";
        }
        public sealed class AnyCollisionController : WebApiController
        {
            [Route(HttpVerbs.Any, "/Value")] public string A() => "a";
            [Route(HttpVerbs.Post, "/value")] public string B() => "b";
        }
        public class InheritedBase : WebApiController { [Route(HttpVerbs.Get, "/Value")] public string Get() => "base"; }
        public sealed class InheritedA : InheritedBase { }
        public sealed class InheritedB : InheritedBase { }
        public sealed class AliasController : WebApiController
        {
            [Route(HttpVerbs.Get, "/Value/{id}")]
            [Route(HttpVerbs.Get, "/value/{id}")]
            public string Get(string id) => "get:" + id;
            [Route(HttpVerbs.Post, "/VALUE/{value}")]
            public string Post(string value) => "post:" + value;
        }
        public sealed class IntegerController : WebApiController
        {
            [Route(HttpVerbs.Get, "/GetTest/{id?}")]
            public string GetTest(int? id) => $"GetTest success [id={id}]!";
        }
        public sealed class BodyController : WebApiController
        {
            [Route(HttpVerbs.Post, "/Value/{id}")]
            public string Post(string id, [QueryField] string? tag, [JsonData] Input input)
            {
                if (input is null) throw new System.NullReferenceException();
                return id + "|" + tag + "|" + input.Value;
            }
        }
        public sealed class Input { public string? Value { get; set; } }
        public sealed class RenamedAliasController : WebApiController
        {
            [Route(HttpVerbs.Get, "/Value/{id}")]
            [Route(HttpVerbs.Get, "/value/{ID}")]
            public string Get() => "alias";
        }
    }
}
