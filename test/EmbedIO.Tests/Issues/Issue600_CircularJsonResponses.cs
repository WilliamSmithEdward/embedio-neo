using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbedIO.Serialization;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue600_CircularJsonResponses
    {
        [Test]
        public void DefaultSerializationStillRejectsCycles()
        {
            var node = new Node { Name = "root" };
            node.Next = node;
            Assert.Throws<JsonException>(() => Json.Serialize(node));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task ExplicitSerializerIgnoresCyclesWithoutChangingDefaults(bool bufferResponse, bool twoNodes)
        {
            var node = new Node { Name = "root" };
            node.Next = twoNodes ? new Node { Name = "child", Next = node } : node;
            var options = Json.CreateOptions();
            options.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            var serializer = ResponseSerializer.Json(bufferResponse, options);
            // The callback snapshots caller-owned options rather than retaining them.
            options.ReferenceHandler = null;
            using var server = new TestWebServer();
            server.OnAny(context => context.SendDataAsync(serializer, node)).Start();

            for (var request = 0; request < 2; request++)
            {
                using var response = await server.Client.GetAsync("/");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = document.RootElement;
                Assert.That(root.GetProperty("Name").GetString(), Is.EqualTo("root"));
                var next = root.GetProperty("Next");
                if (twoNodes)
                {
                    Assert.That(next.GetProperty("Name").GetString(), Is.EqualTo("child"));
                    next = next.GetProperty("Next");
                }

                Assert.That(next.ValueKind, Is.EqualTo(JsonValueKind.Null));
            }

            Assert.Throws<JsonException>(() => Json.Serialize(node));
        }

        [Test]
        public void IgnoreCyclesPreservesRepeatedReferencesOutsideTheCurrentPath()
        {
            var child = new Node { Name = "shared" };
            var options = Json.CreateOptions();
            options.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            using var document = JsonDocument.Parse(Json.Serialize(new[] { child, child }, options));
            Assert.That(document.RootElement.GetArrayLength(), Is.EqualTo(2));
            Assert.That(document.RootElement[0].GetProperty("Name").GetString(), Is.EqualTo("shared"));
            Assert.That(document.RootElement[1].GetProperty("Name").GetString(), Is.EqualTo("shared"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task BuiltInOptionsHandleObjectTypedDictionaryCycles(bool bufferResponse)
        {
            var data = new Dictionary<string, object> { ["Name"] = "root" };
            data["Self"] = data;
            var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles };
            var serializer = ResponseSerializer.Json(bufferResponse, options);
            using var server = new TestWebServer();
            server.OnAny(context => context.SendDataAsync(serializer, data)).Start();
            using var response = await server.Client.GetAsync("/");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(document.RootElement.GetProperty("Name").GetString(), Is.EqualTo("root"));
            Assert.That(document.RootElement.GetProperty("Self").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        public sealed class Node
        {
            public string Name { get; set; } = string.Empty;
            public Node? Next { get; set; }
        }
    }
}
