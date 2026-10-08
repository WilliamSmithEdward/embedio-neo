using System.IO;
using System;
using System.Linq;
using System.Text;
using EmbedIO.Files;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class ResourceFileProviderTest
    {
        private readonly IFileProvider _fileProvider = new ResourceFileProvider(
            typeof(TestWebServer).Assembly,
            typeof(TestWebServer).Namespace + ".Resources");

        private readonly IMimeTypeProvider _mimeTypeProvider = new MockMimeTypeProvider();

        [TestCase("/index.html", "index.html")]
        [TestCase("/sub/index.html", "index.html")]
        public void MapFile_ReturnsCorrectFileInfo(string requestPath, string name)
        {
            var info = _fileProvider.MapUrlPath(requestPath, _mimeTypeProvider) ?? throw new InvalidOperationException("The stock resource must be mapped.");

            Assert.IsNotNull(info, "info != null");
            Assert.IsTrue(info.IsFile, "info.IsFile == true");
            Assert.AreEqual(name, info.Name, "info.Name has the correct value");
            Assert.AreEqual(StockResource.GetLength(requestPath), info.Length, "info.Length has the correct value");
        }

        [TestCase("/index.html")]
        [TestCase("/sub/index.html")]
        public void OpenFile_ReturnsCorrectContent(string requestPath)
        {
            var info = _fileProvider.MapUrlPath(requestPath, _mimeTypeProvider) ?? throw new InvalidOperationException("The stock resource must be mapped.");
            var expectedText = StockResource.GetText(requestPath, WebServer.DefaultEncoding);

            using var stream = _fileProvider.OpenFile(info.Path) ?? throw new InvalidOperationException("The mapped stock resource must exist.");
            using var reader = new StreamReader(stream, WebServer.DefaultEncoding, false, WebServer.StreamCopyBufferSize, true);
            var actualText = reader.ReadToEnd();

            Assert.AreEqual(expectedText, actualText, "Content is the same as embedded resource");
        }

        [Test]
        public void GetDirectoryEntries_ReturnsEmptyEnumerable()
        {
            var entries = _fileProvider.GetDirectoryEntries(string.Empty, _mimeTypeProvider);
            Assert.IsFalse(entries.Any(), "There are no entries");
        }
    }
}
