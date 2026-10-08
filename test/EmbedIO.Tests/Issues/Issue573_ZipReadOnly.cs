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

namespace EmbedIO.Tests.Issues
{
    public class Issue573_ZipReadOnly
    {
        [TestCase(false)]
        [TestCase(true)]
        public void IndependentProvidersAndExternalReadersShareTheArchive(bool externalFirst)
        {
            using var zip = new ArchiveFile();
            var original = File.ReadAllBytes(zip.Path);
            FileStream? reader = null;
            ZipFileProvider? first = null;
            ZipFileProvider? second = null;
            try
            {
                if (externalFirst) reader = File.Open(zip.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                first = new ZipFileProvider(zip.Path);
                second = new ZipFileProvider(zip.Path);
                reader ??= File.Open(zip.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.That(first.IsImmutable, Is.True);
                Assert.That(second.IsImmutable, Is.True);
                Assert.That(ReadEntry(first), Is.EqualTo("Hello shared ZIP"));
                first.Dispose();
                Assert.That(ReadEntry(second), Is.EqualTo("Hello shared ZIP"));
                Assert.That(reader.CanRead, Is.True);
            }
            finally { second?.Dispose(); first?.Dispose(); reader?.Dispose(); }
            using var exclusive = File.Open(zip.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var contents = new byte[exclusive.Length];
            exclusive.ReadExactly(contents);
            Assert.That(contents, Is.EqualTo(original), "Hosting must not modify the archive.");
        }

        [Test]
        public void ReadOnlyArchiveCanBeHosted()
        {
            using var zip = new ArchiveFile();
            File.SetAttributes(zip.Path, File.GetAttributes(zip.Path) | FileAttributes.ReadOnly);
            using var provider = new ZipFileProvider(zip.Path);
            Assert.That(ReadEntry(provider), Is.EqualTo("Hello shared ZIP"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidArchiveConstructionDoesNotLeaveAnOwnedHandle(bool empty)
        {
            using var zip = new ArchiveFile();
            File.WriteAllBytes(zip.Path, empty ? Array.Empty<byte>() : Encoding.ASCII.GetBytes("not a ZIP"));
            Assert.Throws<InvalidDataException>(() => new ZipFileProvider(zip.Path));
            using var exclusive = File.Open(zip.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.That(exclusive.CanWrite, Is.True, "No GC cycle should be needed to reopen a rejected archive.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReadOnlyStreamOverloadPreservesCallerOwnership(bool leaveOpen)
        {
            using var zip = new ArchiveFile();
            using var source = File.Open(zip.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var provider = new ZipFileProvider(source, leaveOpen);
            Assert.That(ReadEntry(provider), Is.EqualTo("Hello shared ZIP"));
            provider.Dispose();
            Assert.That(source.CanRead, Is.EqualTo(leaveOpen));
            if (leaveOpen) source.Dispose();
            using var exclusive = File.Open(zip.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.That(exclusive.CanWrite, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedFluentConfigurationReleasesTheArchive(bool named)
        {
            using var zip = new ArchiveFile();
            using var server = new WebServer();
            var error = new InvalidOperationException("configuration rejected");
            var caught = Assert.Throws<InvalidOperationException>(() =>
            {
                if (named) server.WithZipFile("zip", "/assets", zip.Path, _ => throw error);
                else server.WithZipFile("/assets", zip.Path, _ => throw error);
            });
            Assert.That(caught, Is.SameAs(error));
            using var exclusive = File.Open(zip.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.That(exclusive.CanWrite, Is.True);
        }

        [TestCase(HttpListenerMode.EmbedIO, CompressionLevel.NoCompression)]
        [TestCase(HttpListenerMode.EmbedIO, CompressionLevel.Optimal)]
        [TestCase(HttpListenerMode.Microsoft, CompressionLevel.NoCompression)]
        [TestCase(HttpListenerMode.Microsoft, CompressionLevel.Optimal)]
        public async Task TwoZipHostsServeContentAndRemainIndependentDuringShutdown(HttpListenerMode mode, CompressionLevel compression)
        {
            using var zip = new ArchiveFile(compression);
            var firstUrl = Resources.GetServerAddress();
            var secondUrl = Resources.GetServerAddress();
            using var first = new WebServer(o => o.WithUrlPrefix(firstUrl).WithMode(mode)).WithZipFile("/assets", zip.Path);
            using var second = new WebServer(o => o.WithUrlPrefix(secondUrl).WithMode(mode)).WithZipFile("shared", "/assets", zip.Path);
            using var firstStop = new CancellationTokenSource();
            using var secondStop = new CancellationTokenSource();
            var firstRun = first.RunAsync(firstStop.Token);
            var secondRun = second.RunAsync(secondStop.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                using var external = File.Open(zip.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var firstGet = client.GetByteArrayAsync(firstUrl + "assets/data.bin");
                var secondGet = client.GetByteArrayAsync(secondUrl + "assets/data.bin");
                Assert.That(await firstGet, Is.EqualTo(zip.Payload));
                Assert.That(await secondGet, Is.EqualTo(zip.Payload));
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, firstUrl + "assets/data.bin"));
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(zip.Payload.Length));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                using var request = new HttpRequestMessage(HttpMethod.Get, secondUrl + "assets/data.bin");
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(19, 91);
                using var range = await client.SendAsync(request);
                Assert.That(range.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                Assert.That(await range.Content.ReadAsByteArrayAsync(), Is.EqualTo(zip.Payload[19..92]));
                using var missing = await client.GetAsync(firstUrl + "assets/missing.txt");
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                firstStop.Cancel();
                await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
                first.Dispose();
                Assert.That(await client.GetStringAsync(secondUrl + "assets/sub/hello%20world.txt"), Is.EqualTo("Hello shared ZIP"));
                Assert.That(external.CanRead, Is.True);
            }
            finally
            {
                firstStop.Cancel();
                secondStop.Cancel();
                await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private static string ReadEntry(ZipFileProvider provider)
        {
            var info = provider.MapUrlPath("/sub/hello%20world.txt", new MockMimeTypeProvider());
            Assert.That(info, Is.Not.Null);
            using var stream = provider.OpenFile(info.Path);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private sealed class ArchiveFile : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embedio-573-" + Guid.NewGuid().ToString("N") + ".zip");
            public byte[] Payload { get; } = new byte[256 * 1024];
            public ArchiveFile(CompressionLevel compression = CompressionLevel.Optimal)
            {
                new Random(573).NextBytes(Payload);
                using var archive = ZipFile.Open(Path, ZipArchiveMode.Create);
                using (var stream = archive.CreateEntry("data.bin", compression).Open()) stream.Write(Payload);
                using var writer = new StreamWriter(archive.CreateEntry("sub/hello world.txt", compression).Open(), WebServer.Utf8NoBomEncoding);
                writer.Write("Hello shared ZIP");
            }
            public void Dispose()
            {
                File.SetAttributes(Path, FileAttributes.Normal);
                File.Delete(Path);
            }
        }
    }
}
