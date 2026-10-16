using System;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.Tests.Issues;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public sealed class MultipartBindingTest
    {
        [TestCase("chunked", HttpStatusCode.OK)]
        [TestCase("gzip", HttpStatusCode.OK)]
        [TestCase("gzip-limit", HttpStatusCode.RequestEntityTooLarge)]
        [TestCase("truncated", HttpStatusCode.BadRequest)]
        public async Task TransportFramingAndDecodedLimitsPreserveHealthyFollowup(string kind, HttpStatusCode expected)
        {
            ArgumentNullException.ThrowIfNull(kind);
            var text = "--edge\r\nContent-Disposition: form-data; name=\"field\"\r\n\r\n"
                + new string('x', kind == "gzip-limit" ? 2000 : 10)
                + (kind == "truncated" ? string.Empty : "\r\n--edge--\r\n");
            var bytes = Encoding.ASCII.GetBytes(text);
            var compressed = kind.StartsWith("gzip", StringComparison.Ordinal);
            if (compressed)
            {
                using var encoded = new MemoryStream();
                using (var gzip = new GZipStream(encoded, CompressionMode.Compress, leaveOpen: true))
                    await gzip.WriteAsync(bytes);
                bytes = encoded.ToArray();
                Assert.That(bytes.Length, Is.LessThan(1024), "Wire bytes must remain below the multipart total limit.");
            }

            var prefix = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = new WebServer(options => options.WithUrlPrefix(prefix)
                .WithMode(HttpListenerMode.EmbedIO).WithSupportCompressedRequests(true));
            var sawChunked = false;
            server.OnAny(async context =>
            {
                if (context.Request.HttpMethod == "GET")
                {
                    await context.SendStringAsync("healthy", "text/plain", Encoding.UTF8);
                    return;
                }

                sawChunked = context.Request.Headers["Transfer-Encoding"] == "chunked";
                using var reader = context.OpenMultipartReader(new MultipartOptions { MaxTotalBytes = 1024, MaxFieldBytes = 4096 });
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync(context.CancellationToken)) != null)
                    await section.Body.CopyToAsync(Stream.Null, context.CancellationToken);
                await context.SendStringAsync("accepted", "text/plain", Encoding.UTF8);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
                using var request = new HttpRequestMessage(HttpMethod.Post, prefix);
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=edge");
                if (compressed) request.Content.Headers.ContentEncoding.Add("gzip");
                if (kind == "chunked") request.Headers.TransferEncodingChunked = true;
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(expected));
                if (kind == "chunked") Assert.That(sawChunked, Is.True);
                Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
            }
            finally
            {
                await stop.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase(false, 4, HttpStatusCode.OK)]
        [TestCase(true, 4, HttpStatusCode.OK)]
        [TestCase(false, 5, HttpStatusCode.RequestEntityTooLarge)]
        [TestCase(true, 5, HttpStatusCode.RequestEntityTooLarge)]
        public async Task BothBindingsStreamFilesAndApplyAttributeLimits(bool generated, int size, HttpStatusCode status)
        {
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated) module.WithController(MultipartBindings.MultipartUploadController);
            else module.WithController<MultipartUploadController>();
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var content = new MultipartFormDataContent("test-boundary");
            content.Add(new StringContent("value"), "description");
            content.Add(new ByteArrayContent(new byte[size]), "upload", "a.bin");
            using var response = await client.PostAsync(server.Url + "api/upload", content);
            Assert.That(response.StatusCode, Is.EqualTo(status));
            if (status == HttpStatusCode.OK)
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("description::5|upload:a.bin:4|"));

            // Rejection or successful disposal must not prevent the next request.
            using var healthy = await client.GetAsync(server.Url + "api/healthy");
            Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task BothBindingsRejectNonMultipartBodies(bool generated)
        {
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            if (generated) module.WithController(MultipartBindings.MultipartUploadController);
            else module.WithController<MultipartUploadController>();
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var body = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(server.Url + "api/upload", body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
        }

        [Test]
        public void OpenApiDescribesDynamicMultipartWithoutInventingFields()
        {
            var document = new OpenApiDocumentBuilder("Uploads", "1")
                .AddController("/api", MultipartBindings.MultipartUploadController)
                .Build(Serialization.Json.CreateOptions());
            var body = document.Root["paths"]?["/api/upload"]?["post"]?["requestBody"];
            Assert.That(body?["required"]?.GetValue<bool>(), Is.True);
            var schema = body?["content"]?["multipart/form-data"]?["schema"];
            Assert.That(schema?["type"]?.GetValue<string>(), Is.EqualTo("object"));
            Assert.That(schema?["additionalProperties"]?.GetValue<bool>(), Is.True);
            Assert.That(schema?["properties"], Is.Null);
            Assert.That(document.Problems, Is.Empty);
            var responses = document.Root["paths"]?["/api/upload"]?["post"]?["responses"];
            Assert.That(responses?["400"], Is.Not.Null);
            Assert.That(responses?["413"], Is.Not.Null);
            Assert.That(responses?["415"], Is.Not.Null);
            var folder = Environment.GetEnvironmentVariable("EMBEDIO_OPENAPI_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "openapi");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "multipart.json"), document.ToJson(indented: true));
        }

        [Test]
        public void OpenApiRejectsDuplicateAndMixedBodyReaders()
        {
            var builder = new OpenApiDocumentBuilder("Invalid uploads", "1")
                .AddController("/api", MultipartBindings.MultipartMixedController);
            Assert.Throws<OpenApiDocumentException>(() => builder.Build(Serialization.Json.CreateOptions()));
            var incomplete = builder.Build(Serialization.Json.CreateOptions(), allowIncomplete: true);
            Assert.That(incomplete.Problems, Has.Count.EqualTo(3));
        }
    }

    [GeneratedControllerBinding(typeof(MultipartUploadController))]
    [GeneratedControllerBinding(typeof(MultipartMixedController))]
    internal static partial class MultipartBindings
    {
    }

    public sealed class MultipartUploadController : WebApiController
    {
        [Route(HttpVerbs.Post, "/upload")]
        public async Task<string> Upload([MultipartForm(MaxFileBytes = 4)] MultipartReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);
            using (reader)
            {
                var result = new StringBuilder();
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync(CancellationToken)) != null)
                {
                    using var bytes = new MemoryStream();
                    await section.Body.CopyToAsync(bytes, CancellationToken);
                    result.Append(section.Name).Append(':').Append(section.FileName).Append(':').Append(bytes.Length).Append('|');
                }

                return result.ToString();
            }
        }

        [Route(HttpVerbs.Get, "/healthy")]
        public string Healthy() => "ok";
    }

    public sealed class MultipartMixedController : WebApiController
    {
        [Route(HttpVerbs.Post, "/json")]
        public void Json([MultipartForm] MultipartReader multipart, [JsonData] string body) { }

        [Route(HttpVerbs.Post, "/form")]
        public void Form([MultipartForm] MultipartReader multipart, [FormData] NameValueCollection body) { }

        [Route(HttpVerbs.Post, "/duplicate")]
        public void Duplicate([MultipartForm] MultipartReader first, [MultipartForm] MultipartReader second) { }
    }
}
