using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Pins the FileModule request branches that the rest of the suite leaves partly
    // covered (negotiation failures, variant selection, the prepare callback, MIME
    // fallbacks, handler replacement, cache clearing and default-extension mapping),
    // so the module's resource handling can be restructured against a complete contract.
    public class FileModuleResourcePinningTest
    {
        private static readonly byte[] Script = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"console.log('line {i % 37}');\n")));
        private static readonly DateTime Original = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        private DirectoryInfo _root = null!;

        [SetUp]
        public void CreateRoot() => _root = Directory.CreateTempSubdirectory("embedio-resource-pins-");

        [TearDown]
        public void DeleteRoot() => _root.Delete(true);

        // A client that refuses identity and names nothing the module can produce gets 406,
        // whether the resource is a file (with or without precompressed variants) or a listing.
        [TestCase(false, PrecompressedVariants.None)]
        [TestCase(false, PrecompressedVariants.All)]
        [TestCase(true, PrecompressedVariants.All)]
        public async Task AClientThatRefusesEveryCodingGets406(bool directory, PrecompressedVariants variants)
        {
            Write("app.js", Script, Original);
            Write("app.js.br", Brotli(Script), Original.AddMinutes(1));
            Directory.CreateDirectory(Path.Combine(_root.FullName, "docs"));
            Write(Path.Combine("docs", "readme.txt"), Script, Original);
            var module = Module(variants);
            module.DirectoryLister = DirectoryLister.Html;
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var response = await Send(client, HttpMethod.Get, directory ? "/docs/" : "/app.js", ("Accept-Encoding", "identity;q=0"));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotAcceptable));
            });
        }

        [Test]
        public async Task AVariantPathThatIsADirectoryIsIgnored()
        {
            Write("app.js", Script, Original);
            Directory.CreateDirectory(Path.Combine(_root.FullName, "app.js.br"));
            await TestWebServer.UseAsync(server => server.WithModule(Module(PrecompressedVariants.All)), async client =>
            {
                using var response = await Send(client, HttpMethod.Get, "/app.js", ("Accept-Encoding", "br"));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Headers.ETag?.ToString(), Does.Not.EndWith("-br-file\""), "A directory is not a precompressed file.");
                var coding = response.Content.Headers.ContentEncoding.SingleOrDefault();
                Assert.That(Decode(await response.Content.ReadAsByteArrayAsync(), coding), Is.EqualTo(Script));
            });
        }

        // The negotiated coding's weight is the floor a variant must reach; deflate is negotiable
        // but never precompressed, so it competes only through that floor.
        [TestCase("deflate;q=1, br;q=0.5", "deflate", false)]
        [TestCase("deflate;q=0.5, br;q=1", "br", true)]
        public async Task DeflateNegotiationWeighsVariantsAgainstTheNegotiatedCoding(string acceptEncoding, string coding, bool variant)
        {
            Write("app.js", Script, Original);
            var brotli = Write("app.js.br", Brotli(Script), Original.AddMinutes(1));
            await TestWebServer.UseAsync(server => server.WithModule(Module(PrecompressedVariants.All)), async client =>
            {
                using var response = await Send(client, HttpMethod.Get, "/app.js", ("Accept-Encoding", acceptEncoding));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentEncoding.SingleOrDefault(), Is.EqualTo(coding));
                var body = await response.Content.ReadAsByteArrayAsync();
                if (variant)
                {
                    Assert.That(body, Is.EqualTo(brotli), "The variant's own bytes.");
                    Assert.That(response.Headers.ETag?.ToString(), Does.EndWith("-br-file\""));
                }
                else
                {
                    Assert.That(body, Is.Not.EqualTo(brotli));
                    Assert.That(response.Headers.ETag?.ToString(), Does.Not.EndWith("-br-file\""));
                }
            });
        }

        [Test]
        public async Task OnPrepareResponseRunsForVariantsAndTheirNotModifiedReplies()
        {
            Write("app.js", Script, Original);
            Write("app.js.br", Brotli(Script), Original.AddMinutes(1));
            var calls = new List<string>();
            var module = Module(PrecompressedVariants.All);
            module.OnPrepareResponse = (context, info) => calls.Add(info.Name + ":" + context.Response.StatusCode);
            Assert.That(module.OnPrepareResponse, Is.Not.Null);
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var first = await Send(client, HttpMethod.Get, "/app.js", ("Accept-Encoding", "br"));
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(first.Headers.ETag?.ToString(), Does.EndWith("-br-file\""));
                var tag = first.Headers.ETag?.ToString() ?? throw new AssertionException("Missing entity tag.");
                using var second = await Send(client, HttpMethod.Get, "/app.js", ("Accept-Encoding", "br"), ("If-None-Match", tag));
                Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                Assert.That(calls, Is.EqualTo(new[] { "app.js:200", "app.js:304" }), "The callback sees the original resource for both replies.");
            });
        }

        [TestCase(null)]
        [TestCase("not a mime type")]
        public async Task AProviderWithoutAUsableContentTypeGetsTheDefaultMimeType(string? contentType)
        {
            var provider = new CountingProvider(Script, contentType);
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider)), async client =>
            {
                using var response = await Send(client, HttpMethod.Get, "/file.bin");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(MimeType.Default));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Script));
            });
        }

        [Test]
        public async Task ADirectoryWithoutAListerUsesOnDirectoryNotListable()
        {
            Directory.CreateDirectory(Path.Combine(_root.FullName, "docs"));
            var refusing = Module(PrecompressedVariants.None);
            refusing.DefaultDocument = null;
            Assert.That(refusing.DirectoryLister, Is.Null);
            await TestWebServer.UseAsync(server => server.WithModule(refusing), async client =>
            {
                using var response = await Send(client, HttpMethod.Get, "/docs/");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            });

            FileRequestHandlerCallback teapot = (context, info) =>
            {
                context.Response.StatusCode = 418;
                return Task.CompletedTask;
            };
            var replaced = Module(PrecompressedVariants.None);
            replaced.DefaultDocument = null;
            replaced.OnDirectoryNotListable = teapot;
            Assert.That(replaced.OnDirectoryNotListable, Is.SameAs(teapot));
            await TestWebServer.UseAsync(server => server.WithModule(replaced), async client =>
            {
                using var response = await Send(client, HttpMethod.Get, "/docs/");
                Assert.That((int)response.StatusCode, Is.EqualTo(418));
            });
        }

        [Test]
        public async Task OnMethodNotAllowedCanBeReplaced()
        {
            Write("app.js", Script, Original);
            await TestWebServer.UseAsync(server => server.WithModule(Module(PrecompressedVariants.None)), async client =>
            {
                using var response = await Send(client, HttpMethod.Post, "/app.js");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
            });

            FileRequestHandlerCallback teapot = (context, info) =>
            {
                context.Response.StatusCode = 418;
                return Task.CompletedTask;
            };
            var replaced = Module(PrecompressedVariants.None);
            replaced.OnMethodNotAllowed = teapot;
            Assert.That(replaced.OnMethodNotAllowed, Is.SameAs(teapot));
            await TestWebServer.UseAsync(server => server.WithModule(replaced), async client =>
            {
                using var response = await Send(client, HttpMethod.Post, "/app.js");
                Assert.That((int)response.StatusCode, Is.EqualTo(418));
            });
        }

        [Test]
        public async Task ClearCacheForcesAFreshReadOfTheProvider()
        {
            var provider = new CountingProvider(Script, "application/javascript");
            using var cache = new FileCache();
            var module = new FileModule("/", provider) { Cache = cache };
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                for (var i = 0; i < 2; i++)
                {
                    using var response = await Send(client, HttpMethod.Get, "/file.bin");
                    Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Script));
                }

                Assert.That(provider.OpenCalls, Is.EqualTo(1), "The second request is served from the cache.");
                module.ClearCache();
                using var fresh = await Send(client, HttpMethod.Get, "/file.bin");
                Assert.That(await fresh.Content.ReadAsByteArrayAsync(), Is.EqualTo(Script));
                Assert.That(provider.OpenCalls, Is.EqualTo(2), "Clearing the cache reads the provider again.");
            });
        }

        [Test]
        public async Task TheDefaultExtensionMapsPlainNamesButNotTheRootPath()
        {
            Write("page.html", Script, Original);
            Directory.CreateDirectory(Path.Combine(_root.FullName, "sub"));
            Write("sub.html", Script, Original);
            var module = Module(PrecompressedVariants.None);
            module.DefaultDocument = null;
            module.DefaultExtension = ".html";
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var page = await Send(client, HttpMethod.Get, "/page");
                Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await page.Content.ReadAsByteArrayAsync(), Is.EqualTo(Script));
                using var sibling = await Send(client, HttpMethod.Get, "/sub");
                Assert.That(sibling.StatusCode, Is.EqualTo(HttpStatusCode.OK), "A file with the default extension wins over a directory of the same name.");
                using var root = await Send(client, HttpMethod.Get, "/");
                Assert.That(root.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "The root path never takes the default extension; it stays the unlisted directory.");
            });
        }

        private FileModule Module(PrecompressedVariants variants)
            => new("/", new FileSystemProvider(_root.FullName, false)) { PrecompressedVariants = variants };

        private byte[] Write(string name, byte[] content, DateTime modifiedUtc)
        {
            var path = Path.Combine(_root.FullName, name);
            File.WriteAllBytes(path, content);
            File.SetLastWriteTimeUtc(path, modifiedUtc);
            return content;
        }

        private static byte[] Brotli(byte[] content)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, true))
                brotli.Write(content, 0, content.Length);
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

        private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(method, path);
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
            return await client.SendAsync(request).ConfigureAwait(false);
        }

        private sealed class CountingProvider : IFileProvider
        {
            private readonly byte[] _bytes;
            private readonly string? _contentType;

            internal CountingProvider(byte[] bytes, string? contentType)
            {
                _bytes = bytes;
                _contentType = contentType;
            }

            internal int OpenCalls { get; private set; }

            public bool IsImmutable => true;

            public event Action<string>? ResourceChanged { add { } remove { } }

            public void Start(CancellationToken cancellationToken) { }

            public MappedResourceInfo? MapUrlPath(string requestPath, IMimeTypeProvider mimeTypeProvider)
                => requestPath == "/file.bin" ? MappedResourceInfo.ForFile("file", "file.bin", Original, _bytes.Length, _contentType) : null;

            public Stream? OpenFile(string path)
            {
                OpenCalls++;
                return new MemoryStream(_bytes, false);
            }

            public IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mimeTypeProvider) => Array.Empty<MappedResourceInfo>();
        }
    }
}
