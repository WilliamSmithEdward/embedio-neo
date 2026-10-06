using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue566_IdentityContentEncoding
    {
        public static IEnumerable VideoCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
                foreach (var cached in new[] { false, true })
                    foreach (var zip in new[] { false, true })
                        foreach (var accept in new string?[] { null, "", "identity" })
                            yield return new object?[] { mode, cached, zip, accept };
        }

        [TestCaseSource(nameof(VideoCases))]
        public async Task UncompressedVideoOmitsContentEncodingForFullPartialHeadAndConditionalResponses(HttpListenerMode mode, bool cached, bool zip, string? accept)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-566-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var payload = new byte[128];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;
            await File.WriteAllBytesAsync(Path.Combine(directory, "video.mp4"), payload);
            var archive = directory + ".zip";
            if (zip) ZipFile.CreateFromDirectory(directory, archive);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            if (zip) server.WithZipFile("/", archive, module => module.ContentCaching = cached);
            else server.WithStaticFolder("/", directory, false, module => module.ContentCaching = cached);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var head = await Send(client, url + "video.mp4", HttpMethod.Head, accept);
                head.EnsureSuccessStatusCode();
                AssertUncompressed(head);
                Assert.That(head.Content.Headers.ContentType!.MediaType, Is.EqualTo("video/mp4"));
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(payload.Length));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);

                using var range = await Send(client, url + "video.mp4", HttpMethod.Get, accept, r => r.Headers.Range = new RangeHeaderValue(4, 19));
                Assert.That(range.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                AssertUncompressed(range);
                Assert.That(range.Content.Headers.ContentRange!.ToString(), Is.EqualTo("bytes 4-19/128"));
                Assert.That(await range.Content.ReadAsByteArrayAsync(), Is.EqualTo(payload[4..20]));
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using var full = await Send(client, url + "video.mp4", HttpMethod.Get, accept);
                    full.EnsureSuccessStatusCode();
                    AssertUncompressed(full);
                    Assert.That(await full.Content.ReadAsByteArrayAsync(), Is.EqualTo(payload));
                }
                using var conditional = await Send(client, url + "video.mp4", HttpMethod.Get, accept, r => r.Headers.IfNoneMatch.Add(head.Headers.ETag!));
                Assert.That(conditional.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                AssertUncompressed(conditional);
                Assert.That(await conditional.Content.ReadAsByteArrayAsync(), Is.Empty);
                using var rejected = await Send(client, url + "video.mp4", HttpMethod.Get, "identity;q=0, *;q=0");
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.NotAcceptable));
                Assert.That(rejected.Headers.Vary, Does.Contain("Accept-Encoding"));
                Assert.That(rejected.Content.Headers.ContentEncoding, Does.Not.Contain("identity"));
            }
            finally
            {
                stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose();
                Directory.Delete(directory, true); if (zip) File.Delete(archive);
            }
        }

        public static IEnumerable DynamicCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
                foreach (var buffered in new[] { false, true })
                    foreach (var accept in new[] { "identity", "gzip", "deflate" })
                        yield return new object[] { mode, buffered, accept };
        }

        [TestCaseSource(nameof(DynamicCases))]
        public async Task TextAndJsonHeadersDescribeActualBytesAndKeepValidCompression(HttpListenerMode mode, bool buffered, string accept)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/text", HttpVerbs.Get, async context =>
                {
                    context.Response.ContentType = MimeType.PlainText;
                    context.Response.Headers[HttpHeaderNames.ContentEncoding] = "stale";
                    using var writer = context.OpenResponseText(WebServer.Utf8NoBomEncoding, buffered);
                    await writer.WriteAsync("café 漢字");
                }))
                .WithWebApi("/json", ResponseSerializer.Json(buffered), module => module.WithController<EncodingController>());
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                foreach (var path in new[] { "text", "json/value" })
                {
                    using var response = await Send(client, url + path, HttpMethod.Get, accept);
                    response.EnsureSuccessStatusCode();
                    if (accept == "identity") AssertUncompressed(response);
                    else Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { accept }));
                    var bytes = await Decode(response, accept);
                    if (path == "text") Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("café 漢字"));
                    else Assert.That(JsonSerializer.Deserialize<string>(bytes), Is.EqualTo("café 漢字"));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, false, "gzip")]
        [TestCase(HttpListenerMode.EmbedIO, true, "gzip")]
        [TestCase(HttpListenerMode.Microsoft, false, "gzip")]
        [TestCase(HttpListenerMode.Microsoft, true, "gzip")]
        [TestCase(HttpListenerMode.EmbedIO, false, "deflate")]
        [TestCase(HttpListenerMode.EmbedIO, true, "deflate")]
        [TestCase(HttpListenerMode.Microsoft, false, "deflate")]
        [TestCase(HttpListenerMode.Microsoft, true, "deflate")]
        public async Task ForcedFileCompressionKeepsItsHeaderAndSeparateIdentityRepresentation(HttpListenerMode mode, bool cached, string encoding)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-566-compress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var bytes = Encoding.UTF8.GetBytes(new string('x', 4096));
            await File.WriteAllBytesAsync(Path.Combine(directory, "video.mp4"), bytes);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithStaticFolder("/", directory, false, module => module.ContentCaching = cached);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var accept = encoding + ", identity;q=0";
                using var compressed = await Send(client, url + "video.mp4", HttpMethod.Get, accept, r => r.Headers.Range = new RangeHeaderValue(0, 7));
                Assert.That(compressed.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Ranges remain ignored when the client forces compression.");
                Assert.That(compressed.Content.Headers.ContentRange, Is.Null);
                Assert.That(compressed.Content.Headers.ContentEncoding, Is.EqualTo(new[] { encoding }));
                Assert.That(await Decode(compressed, encoding), Is.EqualTo(bytes));
                using var head = await Send(client, url + "video.mp4", HttpMethod.Head, accept);
                Assert.That(head.Content.Headers.ContentEncoding, Is.EqualTo(new[] { encoding }));
                Assert.That(head.Headers.ETag, Is.EqualTo(compressed.Headers.ETag));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                using var conditional = await Send(client, url + "video.mp4", HttpMethod.Get, accept, r => r.Headers.IfNoneMatch.Add(compressed.Headers.ETag!));
                Assert.That(conditional.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                Assert.That(conditional.Content.Headers.ContentEncoding, Is.EqualTo(new[] { encoding }));
                using var identity = await Send(client, url + "video.mp4", HttpMethod.Get, "identity", r => r.Headers.IfNoneMatch.Add(compressed.Headers.ETag!));
                Assert.That(identity.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                AssertUncompressed(identity);
                Assert.That(identity.Headers.ETag, Is.Not.EqualTo(compressed.Headers.ETag));
                Assert.That(await identity.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
        }

        [TestCase(HttpListenerMode.EmbedIO, "identity;q=1, *;q=0")]
        [TestCase(HttpListenerMode.Microsoft, "identity;q=1, *;q=0")]
        [TestCase(HttpListenerMode.EmbedIO, "gzip;q=0, deflate;q=0, identity")]
        [TestCase(HttpListenerMode.Microsoft, "gzip;q=0, deflate;q=0, identity")]
        public async Task NegotiationClearsAStaleHeaderWhenNoTransformationIsSelected(HttpListenerMode mode, string accept)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    context.Response.Headers[HttpHeaderNames.ContentEncoding] = "gzip";
                    context.Response.Headers[HttpHeaderNames.Vary] = "Origin";
                    Assert.That(context.Request.TryNegotiateContentEncoding(true, out var method, out var prepare), Is.True);
                    Assert.That(method, Is.EqualTo(CompressionMethod.None));
                    prepare(context.Response);
                    context.Response.ContentLength64 = 3;
                    await context.Response.OutputStream.WriteAsync(new byte[] { 0, 128, 255 });
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await Send(client, url, HttpMethod.Get, accept);
                response.EnsureSuccessStatusCode();
                AssertUncompressed(response);
                Assert.That(response.Headers.Vary, Does.Contain("Origin"));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 0, 128, 255 }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static void AssertUncompressed(HttpResponseMessage response)
        {
            Assert.That(response.Content.Headers.Contains(HttpHeaderNames.ContentEncoding), Is.False, "The field must be absent, rather than empty or identity.");
            Assert.That(response.Content.Headers.ContentEncoding, Is.Empty);
            Assert.That(response.Headers.Vary, Does.Contain("Accept-Encoding"));
        }

        private static async Task<HttpResponseMessage> Send(HttpClient client, string url, HttpMethod method, string? accept, Action<HttpRequestMessage>? configure = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (accept != null) Assert.That(request.Headers.TryAddWithoutValidation(HttpHeaderNames.AcceptEncoding, accept), Is.True);
            configure?.Invoke(request);
            return await client.SendAsync(request);
        }

        private static async Task<byte[]> Decode(HttpResponseMessage response, string encoding)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (encoding == "identity") return bytes;
            using var input = new MemoryStream(bytes);
            using Stream decoder = encoding == "gzip" ? new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            await decoder.CopyToAsync(output);
            return output.ToArray();
        }

        public sealed class EncodingController : WebApiController
        {
            [Route(HttpVerbs.Get, "/value")]
            public string Value() => "café 漢字";
        }
    }
}
