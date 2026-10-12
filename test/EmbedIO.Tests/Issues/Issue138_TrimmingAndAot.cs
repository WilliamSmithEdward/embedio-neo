using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Serialization;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #138, first increment: AOT-safe JSON overloads that take source-generated metadata,
    // options-based serializers that resolve through the options' context, and the trimming
    // annotations on the reflection-based APIs. Native AOT itself is exercised by the
    // EmbedIO.AotSmoke host in CI.
    public class Issue138_TrimmingAndAot
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [Test]
        public void LegacyNullOptionsDeserializeRetainsDefaultInputHandling()
        {
            var dto = Json.Deserialize<Dto>("{\"id\":\"7\",\"name\":\"legacy\",}", null);
            Assert.That(dto?.Id, Is.EqualTo(7));
            Assert.That(dto?.Name, Is.EqualTo("legacy"));
        }

        [Test]
        public void LegacyNullOptionsSerializeRetainsOptionsValidation()
        {
            var error = Assert.Throws<ArgumentNullException>(() =>
                InvalidInput.Invoke((Func<object?, JsonSerializerOptions, string>)Json.Serialize, new Dto(), null));
            Assert.That(error?.ParamName, Is.EqualTo("options"));
        }

        [Test]
        public async Task LegacySerializerReceivesNullDataOverRealHttp()
        {
            var url = Resources.GetServerAddress();
            var calls = 0;
            object? suppliedData = new object();
            ResponseSerializerCallback serializer = (context, data) =>
            {
                suppliedData = data;
                Interlocked.Increment(ref calls);
                return ResponseSerializer.Default(context, data);
            };
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new RoutingModule("/")
                    .OnGet("/default", (context, _) => context.SendDataAsync(ResponseSerializer.Default, null))
                    .OnGet("/custom", (context, _) => context.SendDataAsync(serializer, null)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = Deadline };
            try
            {
                foreach (var route in new[] { "default", "custom" })
                {
                    using var response = await client.GetAsync(url + route);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("null"));
                }
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
                Assert.That(suppliedData, Is.Null);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(Deadline);
            }
        }

        [Test]
        public async Task TypedOverloadsSendAndReadJsonOverRealHttp()
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new RoutingModule("/")
                    .OnGet("/item", (context, _) => context.SendDataWithMetadataAsync(new Dto { Id = 7, Name = "seven" }, TestJson.Default.Dto))
                    .OnPost("/item", async (context, _) =>
                    {
                        var dto = await context.GetRequestDataWithMetadataAsync(TestJson.Default.Dto) ?? throw HttpException.BadRequest("Missing.");
                        dto.Id++;
                        await context.SendDataWithMetadataAsync(dto, TestJson.Default.Dto);
                    }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = Deadline };
            try
            {
                using (var got = await client.GetAsync(url + "item"))
                {
                    Assert.That(got.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
                    Assert.That(got.Content.Headers.ContentType?.CharSet, Is.EqualTo("utf-8"));
                    Assert.That(await got.Content.ReadAsStringAsync(), Is.EqualTo("{\"Id\":7,\"Name\":\"seven\"}"));
                }
                using (var posted = await client.PostAsync(url + "item", new StringContent("{\"Id\":1,\"Name\":\"one\"}", Encoding.UTF8, "application/json")))
                    Assert.That(await posted.Content.ReadAsStringAsync(), Is.EqualTo("{\"Id\":2,\"Name\":\"one\"}"));
                using (var invalid = await client.PostAsync(url + "item", new StringContent("{oops", Encoding.UTF8, "application/json")))
                    Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "Malformed JSON is a client error, as with the default deserializer.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(Deadline);
            }
        }

        [Test]
        public void JsonHelpersWithTypeInfoRoundTrip()
        {
            var json = Json.SerializeWithMetadata(new Dto { Id = 3, Name = "three" }, TestJson.Default.Dto);
            Assert.That(json, Is.EqualTo("{\"Id\":3,\"Name\":\"three\"}"));
            var dto = Json.DeserializeWithMetadata(json, TestJson.Default.Dto);
            Assert.That(dto?.Id, Is.EqualTo(3));
            Assert.That(dto?.Name, Is.EqualTo("three"));
            Assert.That(() => InvalidInput.Invoke((Func<Dto, System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dto>, string>)Json.SerializeWithMetadata, new Dto(), null), Throws.ArgumentNullException);
            var error = Assert.Throws<ArgumentNullException>(() => InvalidInput.Invoke(
                (Func<string, System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dto>, Dto?>)Json.DeserializeWithMetadata, json, null));
            Assert.That(error?.ParamName, Is.EqualTo("typeInfo"));
        }

        // Options carrying only a source-generated context resolve through it; no reflection
        // resolver is involved, so the same call works in trimmed and AOT applications.
        [Test]
        public async Task OptionsWithASourceGeneratedContextResolveThroughIt()
        {
            var options = new JsonSerializerOptions { TypeInfoResolver = TestJson.Default };
            Assert.That(Json.Serialize(new Dto { Id = 1, Name = "a" }, options), Is.EqualTo("{\"Id\":1,\"Name\":\"a\"}"));
            Assert.That(Json.Serialize(null, options), Is.EqualTo("null"));
            Assert.That(() => Json.Serialize(new Uri("http://example.com/"), options), Throws.InstanceOf<NotSupportedException>(),
                "A type the context does not declare is not resolved by reflection.");

            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new RoutingModule("/").OnPost("/echo", async (context, _) =>
                {
                    var dto = await context.GetRequestDataAsync(RequestDeserializer.Json<Dto>(options));
                    await context.SendDataAsync(ResponseSerializer.Json(options), dto);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = Deadline };
            try
            {
                using var echoed = await client.PostAsync(url + "echo", new StringContent("{\"Id\":5,\"Name\":\"five\"}", Encoding.UTF8, "application/json"));
                Assert.That(await echoed.Content.ReadAsStringAsync(), Is.EqualTo("{\"Id\":5,\"Name\":\"five\"}"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(Deadline);
            }
        }

        // Reflection-based APIs carry the annotations, so a trimmed or AOT application sees a
        // warning at its call site instead of a failure at run time.
        [TestCase(typeof(Json), nameof(Json.CreateOptions))]
        [TestCase(typeof(WebApiModuleExtensions), nameof(WebApiModuleExtensions.WithController))]
        [TestCase(typeof(WebApiModule), nameof(WebApiModule.RegisterController))]
        [TestCase(typeof(RoutingModule), nameof(RoutingModule.AddFrom))]
        [TestCase(typeof(RoutingModuleExtensions), nameof(RoutingModuleExtensions.WithHandlersFrom))]
        public void ReflectionBasedApisAreAnnotated(Type type, string name)
        {
            ArgumentNullException.ThrowIfNull(type);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Where(m => m.Name == name).ToList();
            Assert.That(methods, Is.Not.Empty);
            foreach (var method in methods)
                Assert.That(method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>(), Is.Not.Null, method.ToString());
        }

        [Test]
        public void TypedAndOptionsBasedApisAreNotAnnotated()
        {
            var typed = typeof(HttpContextExtensions).GetMethods().Where(m => (m.Name == nameof(HttpContextExtensions.SendDataWithMetadataAsync) || m.Name == nameof(HttpContextExtensions.GetRequestDataWithMetadataAsync))
                && m.GetParameters().Any(p => p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(System.Text.Json.Serialization.Metadata.JsonTypeInfo<>))).ToList();
            Assert.That(typed, Has.Count.EqualTo(2));
            Assert.That(typed.Select(m => m.GetCustomAttribute<RequiresUnreferencedCodeAttribute>()), Is.All.Null);
            var serialize = typeof(Json).GetMethod(nameof(Json.Serialize), new[] { typeof(object), typeof(JsonSerializerOptions) });
            Assert.That(serialize?.GetCustomAttribute<RequiresUnreferencedCodeAttribute>(), Is.Null);
            Assert.That(typeof(ResponseSerializer).GetMethod(nameof(ResponseSerializer.Json), new[] { typeof(JsonSerializerOptions) })?.GetCustomAttribute<RequiresUnreferencedCodeAttribute>(), Is.Null);
        }

        public sealed class Dto
        {
            public int Id { get; set; }

            public string Name { get; set; } = string.Empty;
        }
    }

    [JsonSerializable(typeof(Issue138_TrimmingAndAot.Dto))]
    internal sealed partial class TestJson : JsonSerializerContext
    {
    }
}
