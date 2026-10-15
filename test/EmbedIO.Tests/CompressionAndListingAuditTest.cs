using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class CompressionAndListingAuditTest
    {
        private static readonly Func<byte[], CompressionMethod, CompressionMethod, byte[]?> Convert =
            (typeof(WebServer).Assembly.GetType("EmbedIO.Internal.CompressionUtility")
                ?.GetMethod("ConvertCompression", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing conversion helper."))
            .CreateDelegate<Func<byte[], CompressionMethod, CompressionMethod, byte[]?>>();

        [Test]
        public void ConversionMatchesIndependentRuntimeEncoding(
            [Values(0, 1, 2, 3, 255)] int sourceMethod,
            [Values(0, 1, 2, 3, 255)] int targetMethod,
            [Values(false, true)] bool empty)
        {
            var payload = empty ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(new string('x', 10000) + "λ boundary");
            var source = Encode(payload, (CompressionMethod)sourceMethod);
            var result = Convert(source, (CompressionMethod)sourceMethod, (CompressionMethod)targetMethod);
            Assert.That(result, Is.EqualTo(Encode(payload, (CompressionMethod)targetMethod)));
            if (sourceMethod == targetMethod || (sourceMethod is 0 or 255) && (targetMethod is 0 or 255))
                Assert.That(result, Is.SameAs(source));
        }

        [Test]
        public void NullConversionRemainsNull()
        {
            var missing = new byte[1][];
            Assert.That(Convert(missing[0], CompressionMethod.Brotli, CompressionMethod.Gzip), Is.Null);
        }

        [TestCase(CompressionMethod.Gzip)]
        [TestCase(CompressionMethod.Deflate)]
        public void MalformedSourceStillFails(CompressionMethod sourceMethod)
            => Assert.Throws<InvalidDataException>(() => Convert(new byte[] { 255, 255, 255, 255 }, sourceMethod, CompressionMethod.None));

        [Test]
        public async Task DirectoryLinksUseUrlSeparatorsAndEscapeNames()
        {
            var now = DateTime.UtcNow;
            var directory = MappedResourceInfo.ForDirectory("root", "root", now);
            var entries = new[]
            {
                MappedResourceInfo.ForDirectory("root/a & b", "a & b", now),
                MappedResourceInfo.ForFile("root/file.txt", "file.txt", now, 5, "text/plain"),
            };
            using var output = new MemoryStream();
            await DirectoryLister.Html.ListDirectoryAsync(directory, "/root/", entries, output, CancellationToken.None);
            var html = WebServer.DefaultEncoding.GetString(output.ToArray());
            Assert.That(html, Does.Contain("href=\"a%20%26%20b/\">a &amp; b</a>"));
            Assert.That(html, Does.Contain("href=\"file.txt\""));
            Assert.That(html, Does.Contain("href='../'"));
        }

        [Test]
        public void DirectoryContentTypeDeclaresItsActualCharset()
        {
            var type = MediaTypeHeaderValue.Parse(DirectoryLister.Html.ContentType);
            Assert.That(type.MediaType, Is.EqualTo("text/html"));
            Assert.That(type.CharSet, Is.EqualTo(WebServer.DefaultEncoding.WebName));
        }

        private static byte[] Encode(byte[] payload, CompressionMethod method)
        {
            if (method is not (CompressionMethod.Gzip or CompressionMethod.Deflate or CompressionMethod.Brotli)) return payload;
            using var output = new MemoryStream();
            using (Stream compressor = method switch
            {
                CompressionMethod.Gzip => new GZipStream(output, CompressionMode.Compress, true),
                CompressionMethod.Deflate => new DeflateStream(output, CompressionMode.Compress, true),
                _ => new BrotliStream(output, CompressionMode.Compress, true),
            }) compressor.Write(payload, 0, payload.Length);
            return output.ToArray();
        }
    }
}
