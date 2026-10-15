using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class UnknownRangeUnitTest
    {
        private static readonly byte[] Payload = { 10, 20, 30, 40, 50 };
        private static readonly DateTime Modified = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestCase("items=1-2")]
        [TestCase("items=100-200")]
        public async Task LegacyRangeHelperIgnoresUnknownUnit(string range)
        {
            bool? selected = null;
            HttpException? failure = null;
            await TestWebServer.UseAsync(server => server.WithAction("/", HttpVerbs.Get, context =>
            {
                try { selected = context.Request.IsRangeRequest(Payload.Length, "\"sample\"", Modified, out _, out _); }
                catch (HttpException error) { failure = error; }
                return Task.CompletedTask;
            }), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                request.Headers.TryAddWithoutValidation(HttpHeaderNames.Range, range);
                using var response = await client.SendAsync(request);
                Assert.That(failure, Is.Null, "An unsupported range unit must not become a byte-range error.");
                Assert.That(selected, Is.False);
            });
        }

        [TestCase(false, "items=1-2")]
        [TestCase(true, "items=1-2")]
        [TestCase(false, "items=100-200")]
        [TestCase(true, "items=100-200")]
        public async Task FileModuleSendsWholeFileForUnknownUnit(bool cached, string range)
        {
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", new Provider()) { ContentCaching = cached }), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/sample.bin");
                request.Headers.TryAddWithoutValidation(HttpHeaderNames.Range, range);
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentRange, Is.Null);
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Payload));
            });
        }

        [TestCase(false, "bytes")]
        [TestCase(true, "bytes")]
        [TestCase(false, "BYTES")]
        [TestCase(true, "BYTES")]
        public async Task FileModuleStillHonorsByteRanges(bool cached, string unit)
        {
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", new Provider()) { ContentCaching = cached }), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/sample.bin");
                request.Headers.TryAddWithoutValidation(HttpHeaderNames.Range, unit + "=1-2");
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                Assert.That(response.Content.Headers.ContentRange?.ToString(), Is.EqualTo("bytes 1-2/5"));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 20, 30 }));
            });
        }

        private sealed class Provider : IFileProvider
        {
            public bool IsImmutable => true;
            public event Action<string>? ResourceChanged { add { } remove { } }
            public void Start(CancellationToken cancellationToken) { }
            public MappedResourceInfo? MapUrlPath(string requestPath, IMimeTypeProvider mimeTypeProvider)
                => requestPath == "/sample.bin" ? MappedResourceInfo.ForFile("sample", "sample.bin", Modified, Payload.Length, MimeType.Default) : null;
            public Stream? OpenFile(string path) => new MemoryStream(Payload, false);
            public IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mimeTypeProvider) => Array.Empty<MappedResourceInfo>();
        }
    }
}
