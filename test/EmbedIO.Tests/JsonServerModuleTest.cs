using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmbedIO.JsonServer;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class JsonServerModuleTest
    {
        private string _path = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), $"embedio-json-{Guid.NewGuid():N}.json");
            File.WriteAllText(_path, "{\"posts\":[{\"id\":1,\"title\":\"original\"}]}");
        }

        [TearDown]
        public void TearDown() => File.Delete(_path);

        [TestCase("/api/", "{\"posts\":[{\"id\":1,\"title\":\"original\"}]}")]
        [TestCase("/api/posts", "[{\"id\":1,\"title\":\"original\"}]")]
        [TestCase("/api/posts/1", "{\"id\":1,\"title\":\"original\"}")]
        public Task ReadsPreserveUpstreamPayloads(string path, string expected)
            => TestWebServer.UseAsync(
                server => server.WithModule(new JsonServerModule(jsonPath: _path)),
                async client =>
                {
                    var actual = await client.GetStringAsync(path);
                    Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected)), Is.True);
                });

        [Test]
        public Task WritesPreserveCrudBehaviorAndArePersistedBeforeResponding()
            => TestWebServer.UseAsync(
                server => server.WithModule(new JsonServerModule(jsonPath: _path)),
                async client =>
                {
                    using var post = await client.PostAsync("/api/posts", new StringContent("{\"id\":2,\"title\":\"added\"}"));
                    Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using (var stored = JsonDocument.Parse(File.ReadAllText(_path)))
                        Assert.That(stored.RootElement.GetProperty("posts").GetArrayLength(), Is.EqualTo(2));

                    using var put = await client.PutAsync("/api/posts/2", new StringContent("{\"title\":\"updated\"}"));
                    Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using (var stored = JsonDocument.Parse(File.ReadAllText(_path)))
                        Assert.That(stored.RootElement.GetProperty("posts")[1].GetProperty("title").GetString(), Is.EqualTo("updated"));

                    using var delete = await client.DeleteAsync("/api/posts/2");
                    Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using (var stored = JsonDocument.Parse(File.ReadAllText(_path)))
                        Assert.That(stored.RootElement.GetProperty("posts").GetArrayLength(), Is.EqualTo(1));
                });

        [TestCase("/api/missing", HttpStatusCode.NotFound)]
        [TestCase("/api/posts/999", HttpStatusCode.BadRequest)]
        [TestCase("/api/posts/1/extra", HttpStatusCode.BadRequest)]
        public Task ErrorStatusIsStable(string path, HttpStatusCode expected)
            => TestWebServer.UseAsync(
                server => server.WithModule(new JsonServerModule(jsonPath: _path)),
                async client =>
                {
                    using var response = await client.GetAsync(path);
                    Assert.That(response.StatusCode, Is.EqualTo(expected));
                });

        [Test]
        public Task ConcurrentWritesAreAllPersisted()
            => TestWebServer.UseAsync(
                server => server.WithModule(new JsonServerModule(jsonPath: _path)),
                async client =>
                {
                    await Task.WhenAll(Enumerable.Range(2, 20).Select(async id =>
                    {
                        using var response = await client.PostAsync("/api/posts", new StringContent($"{{\"id\":{id}}}"));
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    }));
                    using var stored = JsonDocument.Parse(File.ReadAllText(_path));
                    Assert.That(stored.RootElement.GetProperty("posts").GetArrayLength(), Is.EqualTo(21));
                });

        [Test]
        public Task PersistenceErrorsAreReported()
            => TestWebServer.UseAsync(
                server => server.WithModule(new JsonServerModule(jsonPath: _path)),
                async client =>
                {
                    using var lockedFile = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
                    using var response = await client.PostAsync("/api/posts", new StringContent("{\"id\":2}"));
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                });

        [TestCase("POST", "/api/posts")]
        [TestCase("PUT", "/api/posts/1")]
        public Task InvalidJsonDoesNotMutateMemoryOrDisk(string method, string path)
            => TestWebServer.UseAsync(
                server => server.WithModule(new JsonServerModule(jsonPath: _path)),
                async client =>
                {
                    var original = File.ReadAllText(_path);
                    using var request = new HttpRequestMessage(new HttpMethod(method), path)
                    {
                        Content = new StringContent("{\"title\":")
                    };
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                    Assert.That(File.ReadAllText(_path), Is.EqualTo(original));
                    var actual = await client.GetStringAsync("/api/");
                    Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(original)), Is.True);
                });

        [Test]
        public Task PutMergesPropertiesAndPreservesIdentityInPersistedAndReloadedStore()
            => TestWebServer.UseAsync(
                server => server.WithModule(new JsonServerModule(jsonPath: _path)),
                async client =>
                {
                    using var response = await client.PutAsync("/api/posts/1", new StringContent("{\"title\":\"changed\",\"extra\":true}"));
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var stored = JsonDocument.Parse(File.ReadAllText(_path));
                    var row = stored.RootElement.GetProperty("posts")[0];
                    Assert.That(row.GetProperty("id").GetInt32(), Is.EqualTo(1));
                    Assert.That(row.GetProperty("title").GetString(), Is.EqualTo("changed"));
                    Assert.That(row.GetProperty("extra").GetBoolean(), Is.True);
                    await TestWebServer.UseAsync(
                        server => server.WithModule(new JsonServerModule(jsonPath: _path)),
                        async reloaded =>
                        {
                            var actual = await reloaded.GetStringAsync("/api/posts/1");
                            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(row.GetRawText())), Is.True);
                        });
                });
    }
}
