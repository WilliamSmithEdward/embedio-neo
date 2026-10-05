using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ZipFileProviderTest
    {
        [Test]
        public void EncodedNestedPathMapsMetadataAndExactContent()
        {
            using var archive = CreateArchive();
            using var provider = new ZipFileProvider(archive, leaveOpen: true);
            var info = provider.MapUrlPath("/sub/hello%20world.txt", new MockMimeTypeProvider());
            Assert.That(info, Is.Not.Null);
            Assert.That(info!.IsFile, Is.True);
            Assert.That(info.Path, Is.EqualTo("sub/hello world.txt"));
            Assert.That(info.Name, Is.EqualTo("hello world.txt"));
            Assert.That(info.Length, Is.EqualTo(Encoding.UTF8.GetByteCount("Hello ZIP")));
            Assert.That(info.ContentType, Is.EqualTo(MimeType.Default));
            using var stream = provider.OpenFile(info.Path);
            using var reader = new StreamReader(stream);
            Assert.That(reader.ReadToEnd(), Is.EqualTo("Hello ZIP"));
        }

        [TestCase("/")]
        [TestCase("/missing.txt")]
        [TestCase("/sub/HELLO%20world.txt")]
        public void RootAndMissingEntriesDoNotMap(string path)
        {
            using var archive = CreateArchive();
            using var provider = new ZipFileProvider(archive);
            Assert.That(provider.MapUrlPath(path, new MockMimeTypeProvider()), Is.Null);
            Assert.Throws<FileNotFoundException>(() => provider.OpenFile("missing.txt"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisposalRespectsStreamOwnership(bool leaveOpen)
        {
            using var archive = CreateArchive();
            var provider = new ZipFileProvider(archive, leaveOpen);
            provider.Dispose();
            Assert.That(archive.CanRead, Is.EqualTo(leaveOpen));
        }

        [Test]
        public async Task FileModuleServesZipGetHeadAndMissingResources()
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new FileModule("/assets", new ZipFileProvider(CreateArchive())));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
                using var get = await client.GetAsync("/assets/sub/hello%20world.txt");
                Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await get.Content.ReadAsStringAsync(), Is.EqualTo("Hello ZIP"));
                using var request = new HttpRequestMessage(HttpMethod.Head, "/assets/sub/hello%20world.txt");
                using var head = await client.SendAsync(request);
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(9));
                Assert.That(head.Headers.ETag, Is.EqualTo(get.Headers.ETag));
                Assert.That(head.Content.Headers.ContentType?.MediaType, Is.EqualTo(get.Content.Headers.ContentType?.MediaType));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                using var missing = await client.GetAsync("/assets/missing.txt");
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private static MemoryStream CreateArchive()
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("sub/hello world.txt").Open(), new UTF8Encoding(false));
                writer.Write("Hello ZIP");
            }

            stream.Position = 0;
            return stream;
        }
    }
}
