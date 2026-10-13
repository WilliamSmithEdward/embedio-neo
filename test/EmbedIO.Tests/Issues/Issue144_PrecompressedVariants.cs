using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Opt-in serving of precompressed .br and .gz files next to the originals.
    [TestFixture]
    public sealed class Issue144_PrecompressedVariants
    {
        private static readonly byte[] Script = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 800).Select(i => $"console.log('line {i % 37}');\n")));
        private static readonly DateTime Original = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        private string _root = string.Empty;

        [SetUp]
        public void CreateFiles()
        {
            _root = Path.Combine(Path.GetTempPath(), "embedio-issue144-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Write("app.js", Script, Original);
            Write("app.js.br", Brotli(Script), Original.AddMinutes(1));
            Write("app.js.gz", Gzip(Script), Original.AddMinutes(1));
            Write("index.html", Encoding.UTF8.GetBytes("<html>" + new string('a', 4000) + "</html>"), Original);
            Write("index.html.br", Brotli(File.ReadAllBytes(Path.Combine(_root, "index.html"))), Original.AddMinutes(1));
            Write("only-br.js", Script, Original);
            Write("only-br.js.br", Brotli(Script), Original.AddMinutes(1));
            var png = Enumerable.Repeat((byte)7, 4000).ToArray();
            Write("image.png", png, Original);
            Write("image.png.br", Brotli(png), Original.AddMinutes(1));
        }

        [TearDown]
        public void DeleteFiles() => Directory.Delete(_root, true);

        private void Write(string name, byte[] content, DateTime modifiedUtc)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, content);
            File.SetLastWriteTimeUtc(path, modifiedUtc);
        }

        private static byte[] Brotli(byte[] content)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, true))
                brotli.Write(content, 0, content.Length);
            return output.ToArray();
        }

        private static byte[] Gzip(byte[] content)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
                gzip.Write(content, 0, content.Length);
            return output.ToArray();
        }

        private static byte[] Decode(byte[] body, string? coding)
        {
            if (coding == null)
                return body;

            using var input = new MemoryStream(body);
            using Stream decoder = coding == "br" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            decoder.CopyTo(output);
            return output.ToArray();
        }

        private FileModule Module(PrecompressedVariants variants)
            => new("/", new FileSystemProvider(_root, false)) { PrecompressedVariants = variants, DefaultDocument = "index.html" };

        private sealed record Reply(HttpStatusCode Status, string? Coding, string? ContentType, string? ETag, string[] Vary, long? Length, byte[] Body);

        private static async Task<Reply> Get(HttpClient client, string url, string? acceptEncoding, HttpMethod? method = null, params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
            if (acceptEncoding != null)
                request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsByteArrayAsync();
            return new Reply(
                response.StatusCode,
                response.Content.Headers.ContentEncoding.SingleOrDefault(),
                response.Content.Headers.ContentType?.MediaType,
                response.Headers.ETag?.ToString(),
                response.Headers.Vary.ToArray(),
                response.Content.Headers.ContentLength,
                body);
        }

        private static HttpClient Client() => new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });

        private async Task<Reply> Through(PrecompressedVariants variants, string path, string? acceptEncoding, HttpMethod? method = null, params (string Name, string Value)[] headers)
        {
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(Module(variants));
            using var client = Client();
            return await Get(client, server.Url + path, acceptEncoding, method, headers);
        }

        [TestCase("br", "br", "app.js.br")]
        [TestCase("gzip", "gzip", "app.js.gz")]
        [TestCase("gzip, br", "br", "app.js.br")]
        [TestCase("gzip, deflate, br", "br", "app.js.br")]
        [TestCase("gzip;q=1, br;q=0.5", "gzip", "app.js.gz")]
        [TestCase("br;q=0.8, gzip;q=0.9", "gzip", "app.js.gz")]
        [TestCase("*", "br", "app.js.br")]
        public async Task TheMostAcceptableVariantIsSentAsIs(string acceptEncoding, string coding, string file)
        {
            var reply = await Through(PrecompressedVariants.All, "app.js", acceptEncoding);
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.Coding, Is.EqualTo(coding));
            Assert.That(reply.Body, Is.EqualTo(File.ReadAllBytes(Path.Combine(_root, file))), "The file's own bytes.");
            Assert.That(reply.Length, Is.EqualTo(new FileInfo(Path.Combine(_root, file)).Length));
            Assert.That(Decode(reply.Body, reply.Coding), Is.EqualTo(Script));
            Assert.That(reply.Vary, Does.Contain("Accept-Encoding"));
            Assert.That(reply.ETag, Does.EndWith("-" + coding + "-file\""));

            // The original's content type, not the .br or .gz file's.
            var identity = await Through(PrecompressedVariants.All, "app.js", null);
            Assert.That(reply.ContentType, Is.EqualTo(identity.ContentType));
        }

        // A client that prefers gzip, with only a .br file, gets gzip compressed by the module.
        [Test]
        public async Task LessPreferredVariantsYieldToCompression()
        {
            var reply = await Through(PrecompressedVariants.All, "only-br.js", "br;q=0.5, gzip");
            Assert.That(reply.Coding, Is.EqualTo("gzip"));
            Assert.That(reply.ETag, Does.Not.Contain("-file"));
            Assert.That(Decode(reply.Body, "gzip"), Is.EqualTo(Script));
        }

        [TestCase(null)]
        [TestCase("identity")]
        [TestCase("br;q=0")]
        [TestCase("br;q=0, gzip;q=0")]
        public async Task ClientsThatDoNotAskGetTheOriginal(string? acceptEncoding)
        {
            var reply = await Through(PrecompressedVariants.All, "app.js", acceptEncoding);
            Assert.That(reply.Coding, Is.Null);
            Assert.That(reply.Body, Is.EqualTo(Script));
        }

        // Refusing every coding still leaves the uncompressed representation acceptable (RFC 9110, 12.5.3),
        // unless identity itself is refused.
        [TestCase("br;q=0", true, true)]
        [TestCase("br;q=0, gzip;q=0, deflate;q=0", true, true)]
        [TestCase("br;q=0", false, true)]
        [TestCase("br;q=0, identity;q=0", true, false)]
        [TestCase("br;q=0, *;q=0", true, false)]
        [TestCase("br;q=0, identity;q=0.5, *;q=0", true, true)]
        public void RefusedCodingsLeaveIdentityAcceptable(string acceptEncoding, bool preferCompression, bool acceptable)
        {
            var list = new QValueList(true, acceptEncoding);
            Assert.That(list.TryNegotiateContentEncoding(preferCompression, out var method, out var name), Is.EqualTo(acceptable));
            if (acceptable)
            {
                Assert.That(method, Is.EqualTo(CompressionMethod.None));
                Assert.That(name, Is.EqualTo(CompressionMethodNames.None));
            }
        }

        // Without the setting, nothing changes: the module compresses the file itself.
        [Test]
        public async Task DisabledByDefault()
        {
            Assert.That(new FileModule("/", new FileSystemProvider(_root, false)).PrecompressedVariants, Is.EqualTo(PrecompressedVariants.None));
            var reply = await Through(PrecompressedVariants.None, "app.js", "gzip");
            Assert.That(reply.Coding, Is.EqualTo("gzip"));
            Assert.That(reply.ETag, Does.Not.Contain("-file"));
            Assert.That(reply.Body, Is.Not.EqualTo(File.ReadAllBytes(Path.Combine(_root, "app.js.gz"))));
            Assert.That(Decode(reply.Body, "gzip"), Is.EqualTo(Script));

            var onlyGzip = await Through(PrecompressedVariants.Gzip, "app.js", "br");
            Assert.That(onlyGzip.ETag, Does.Not.Contain("-file"), "Brotli files were not enabled.");
        }

        // The variant's tag differs from the original's, the compressed original's and the .br file's own.
        [Test]
        public async Task EntityTagsAreDistinctPerRepresentation()
        {
            var variant = await Through(PrecompressedVariants.All, "app.js", "gzip");
            var dynamic = await Through(PrecompressedVariants.None, "app.js", "gzip");
            var identity = await Through(PrecompressedVariants.All, "app.js", null);
            var direct = await Through(PrecompressedVariants.All, "app.js.gz", null);
            var tags = new[] { variant.ETag, dynamic.ETag, identity.ETag, direct.ETag };
            Assert.That(tags, Is.Unique);
            Assert.That(tags, Has.None.Null);

            // Requesting the .gz file itself is unchanged: its bytes, no Content-Encoding.
            Assert.That(direct.Coding, Is.Null);
            Assert.That(direct.Body, Is.EqualTo(File.ReadAllBytes(Path.Combine(_root, "app.js.gz"))));
        }

        // A variant older than the original is stale and ignored.
        [Test]
        public async Task StaleVariantsAreIgnored()
        {
            File.SetLastWriteTimeUtc(Path.Combine(_root, "app.js.br"), Original.AddMinutes(-1));
            var reply = await Through(PrecompressedVariants.Brotli, "app.js", "br");
            Assert.That(reply.ETag, Does.Not.Contain("-file"));
            Assert.That(Decode(reply.Body, reply.Coding), Is.EqualTo(Script));
        }

        // A replaced .br file is seen on the next request, past the module's content cache. The variant is mapped
        // on every request, so this holds even for an immutable provider (FileSystemProvider is always one on macOS):
        // the body and its length must come from the same file.
        [TestCase(false)]
        [TestCase(true)]
        public async Task ChangedVariantsAreServedFresh(bool immutable)
        {
            var module = new FileModule("/", new FileSystemProvider(_root, immutable)) { PrecompressedVariants = PrecompressedVariants.Brotli };
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = Client();
            var first = await Get(client, server.Url + "app.js", "br");
            Assert.That(first.ETag, Does.EndWith("-br-file\""));

            var updated = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("let updated = true;\n", 300)));
            Write("app.js.br", Brotli(updated), Original.AddMinutes(5));
            var second = await Get(client, server.Url + "app.js", "br");
            Assert.That(second.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(second.ETag, Is.Not.EqualTo(first.ETag));
            Assert.That(second.Length, Is.EqualTo(second.Body.Length));
            Assert.That(Decode(second.Body, "br"), Is.EqualTo(updated));
        }

        // The original changes after the .br file: the .br file is stale until it is regenerated. Seeing the original
        // change needs a provider that reports changes.
        [Test]
        public async Task ChangedOriginalsMakeVariantsStale()
        {
            var provider = new FileSystemProvider(_root, false);
            if (provider.IsImmutable)
                Assert.Ignore("This platform's FileSystemProvider is always immutable.");

            await using var server = Issue138_GeneratedBinding.RunningServer.Start(new FileModule("/", provider) { PrecompressedVariants = PrecompressedVariants.Brotli });
            using var client = Client();
            var first = await Get(client, server.Url + "app.js", "br");
            Assert.That(first.ETag, Does.EndWith("-br-file\""));

            var updated = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("let updated = true;\n", 300)));
            Write("app.js", updated, Original.AddMinutes(10));
            var third = await Get(client, server.Url + "app.js", "br");
            Assert.That(third.ETag, Does.Not.Contain("-file"));
            Assert.That(Decode(third.Body, third.Coding), Is.EqualTo(updated));
        }

        [Test]
        public async Task ConditionalRequestsUseTheVariantsTag()
        {
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(Module(PrecompressedVariants.Brotli));
            using var client = Client();
            var full = await Get(client, server.Url + "app.js", "br");
            var tag = full.ETag ?? throw new AssertionException("No entity tag.");

            var notModified = await Get(client, server.Url + "app.js", "br", null, ("If-None-Match", tag));
            Assert.That(notModified.Status, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(notModified.ETag, Is.EqualTo(tag));
            Assert.That(notModified.Vary, Does.Contain("Accept-Encoding"));
            Assert.That(notModified.Body, Is.Empty);

            // The original's tag does not match the variant.
            var identity = await Get(client, server.Url + "app.js", null);
            var mismatch = await Get(client, server.Url + "app.js", "br", null, ("If-None-Match", identity.ETag ?? string.Empty));
            Assert.That(mismatch.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(mismatch.Coding, Is.EqualTo("br"));

            var since = await Get(client, server.Url + "app.js", "br", null, ("If-Modified-Since", Original.AddMinutes(2).ToString("R")));
            Assert.That(since.Status, Is.EqualTo(HttpStatusCode.NotModified));
        }

        [Test]
        public async Task HeadSendsTheVariantsHeadersOnly()
        {
            var reply = await Through(PrecompressedVariants.Brotli, "app.js", "br", HttpMethod.Head);
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.Coding, Is.EqualTo("br"));
            Assert.That(reply.Length, Is.EqualTo(new FileInfo(Path.Combine(_root, "app.js.br")).Length));
            Assert.That(reply.Body, Is.Empty);
        }

        // Range offsets address the original; a range request gets the original's bytes, never the variant's.
        [Test]
        public async Task RangeRequestsGetTheOriginal()
        {
            var reply = await Through(PrecompressedVariants.All, "app.js", "br, gzip", null, ("Range", "bytes=10-29"));
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(reply.Coding, Is.Null);
            Assert.That(reply.Body, Is.EqualTo(Script.Skip(10).Take(20).ToArray()));
        }

        // A MIME type the module does not compress is not served precompressed either.
        [Test]
        public async Task MimePolicyIsKept()
        {
            var module = Module(PrecompressedVariants.All);
            module.PreferCompression("image/png", false);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = Client();
            var reply = await Get(client, server.Url + "image.png", "br");
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.Coding, Is.Null);
            Assert.That(reply.Body, Is.EqualTo(File.ReadAllBytes(Path.Combine(_root, "image.png"))));
        }

        [Test]
        public async Task DefaultDocumentsHaveVariantsToo()
        {
            var reply = await Through(PrecompressedVariants.Brotli, string.Empty, "br");
            Assert.That(reply.Coding, Is.EqualTo("br"));
            Assert.That(reply.Body, Is.EqualTo(File.ReadAllBytes(Path.Combine(_root, "index.html.br"))));
        }

        // A client that refuses the original, and every coding the module can produce, can still get a variant.
        [Test]
        public async Task VariantsServeClientsThatRefuseTheOriginal()
        {
            var reply = await Through(PrecompressedVariants.Gzip, "app.js", "gzip, identity;q=0, *;q=0");
            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.Coding, Is.EqualTo("gzip"));
            Assert.That(reply.Body, Is.EqualTo(File.ReadAllBytes(Path.Combine(_root, "app.js.gz"))));
        }

        [Test]
        public async Task ConcurrentClientsGetTheirOwnRepresentation()
        {
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(Module(PrecompressedVariants.All));
            using var client = Client();
            var encodings = new[] { "br", "gzip", null, "gzip, br", "gzip;q=1, br;q=0.1" };
            var replies = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Get(client, server.Url + "app.js", encodings[i % encodings.Length])));
            for (var i = 0; i < replies.Length; i++)
            {
                var expected = encodings[i % encodings.Length] switch
                {
                    "br" or "gzip, br" => "br",
                    "gzip" or "gzip;q=1, br;q=0.1" => "gzip",
                    _ => null,
                };
                Assert.That(replies[i].Coding, Is.EqualTo(expected), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.That(Decode(replies[i].Body, replies[i].Coding), Is.EqualTo(Script));
            }
        }

        // An uncached large variant is streamed; a client that disconnects mid-way leaves the server working.
        [Test]
        public async Task CanceledDownloadsLeaveTheServerWorking()
        {
            var large = new byte[4 * 1024 * 1024];
            new Random(144).NextBytes(large);
            Write("large.js", large, Original);
            Write("large.js.br", large, Original.AddMinutes(1));

            var module = Module(PrecompressedVariants.Brotli);
            module.ContentCaching = false;
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module);
            using var client = Client();
            using (var request = new HttpRequestMessage(HttpMethod.Get, server.Url + "large.js"))
            {
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                Assert.That(response.Content.Headers.ContentEncoding, Does.Contain("br"));
                Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(large.Length));
                using var stream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[4096];
                Assert.That(await stream.ReadAsync(buffer), Is.GreaterThan(0));
            }

            var after = await Get(client, server.Url + "app.js", "br");
            Assert.That(after.Coding, Is.EqualTo("br"));
            Assert.That(Decode(after.Body, "br"), Is.EqualTo(Script));
        }

        [Test]
        public void ConfigurationIsValidatedAndLockedAtStart()
        {
            var module = Module(PrecompressedVariants.None);
            Assert.Throws<ArgumentOutOfRangeException>(() => module.PrecompressedVariants = (PrecompressedVariants)4);
            module.PrecompressedVariants = PrecompressedVariants.All;
            module.Start(CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => module.PrecompressedVariants = PrecompressedVariants.None);
            module.Dispose();
        }
    }
}
