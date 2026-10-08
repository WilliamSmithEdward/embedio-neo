using System;
using EmbedIO.Utilities;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace EmbedIO.Tests.Utilities
{
    public class UrlPathTests
    {
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("does/not/start/with/slash", false)]
        [TestCase("/", true)]
        [TestCase("/starts/with/slash", true)]
        public void IsValid_ReturnsCorrectValue(string? requestPath, bool expectedResult)
            => Assert.AreEqual(expectedResult, UrlPath.IsValid(requestPath));

        [TestCase(true)]
        [TestCase(false)]
        public void Normalize_OnNullUrlPath_ThrowsArgumentNullException(bool isBasePath)
            => Assert.Throws<ArgumentNullException>(() => UrlPath.Normalize(null, isBasePath));

        [TestCase(true)]
        [TestCase(false)]
        public void Normalize_OnEmptyUrlPath_ThrowsArgumentException(bool isBasePath)
            => Assert.Throws<ArgumentException>(() => UrlPath.Normalize(string.Empty, isBasePath));

        [TestCase(true)]
        [TestCase(false)]
        public void Normalize_OnInvalidUrlPath_ThrowsArgumentException(bool isBasePath)
            => Assert.Throws<ArgumentException>(() => UrlPath.Normalize("does/not/start/with/slash", isBasePath));

        [TestCase("/", false, "/")]
        [TestCase("/", true, "/")]
        [TestCase("/starts/with/slash", false, "/starts/with/slash")]
        [TestCase("/starts/with/slash", true, "/starts/with/slash/")]
        [TestCase("//has/multiple///slashes////", false, "/has/multiple/slashes")]
        [TestCase("//has/multiple//slashes////", true, "/has/multiple/slashes/")]
        public void Normalize_ReturnsCorrectValue(string requestPath, bool isBasePath, string? expectedResult)
            => Assert.AreEqual(expectedResult, UrlPath.Normalize(requestPath, isBasePath));

        [TestCase(null, null)]
        [TestCase(null, "/api/")]
        [TestCase("/api/endpoint", null)]
        public void HasPrefix_OnNullParameter_ThrowsArgumentNullException(string? requestPath, string? basePath)
            => Assert.Throws<ArgumentNullException>(() => UrlPath.HasPrefix(requestPath, basePath));

        [TestCase("", "")]
        [TestCase("", "/api/")]
        [TestCase("/api/endpoint", "")]
        public void HasPrefix_OnEmptyParameter_ThrowsArgumentException(string requestPath, string basePath)
            => Assert.Throws<ArgumentException>(() => UrlPath.HasPrefix(requestPath, basePath));

        [TestCase("!!!", "!!!")]
        [TestCase("!!!", "/api/")]
        [TestCase("/api/endpoint", "!!!")]
        public void HasPrefix_OnInvalidParameter_ThrowsArgumentException(string requestPath, string basePath)
            => Assert.Throws<ArgumentException>(() => UrlPath.HasPrefix(requestPath, basePath));

        [TestCase("/api/v1/endpoint", "/api/v1", true)]
        [TestCase("/api/v1/endpoint", "/api/v1/", true)]
        [TestCase("/api/v1/endpoint", "/api/v2", false)]
        [TestCase("/api/v1/endpoint", "/api/v2/", false)]
        public void HasPrefix_ReturnsCorrectValue(string requestPath, string basePath, bool expectedResult)
            => Assert.AreEqual(expectedResult, UrlPath.HasPrefix(requestPath, basePath));

        [TestCase(null, null)]
        [TestCase(null, "/api/")]
        [TestCase("/api/endpoint", null)]
        public void StripPrefix_OnNullParameter_ThrowsArgumentNullException(string? requestPath, string? basePath)
            => Assert.Throws<ArgumentNullException>(() => UrlPath.StripPrefix(requestPath, basePath));

        [TestCase("", "")]
        [TestCase("", "/api/")]
        [TestCase("/api/endpoint", "")]
        public void StripPrefix_OnEmptyParameter_ThrowsArgumentException(string requestPath, string basePath)
            => Assert.Throws<ArgumentException>(() => UrlPath.StripPrefix(requestPath, basePath));

        [TestCase("!!!", "!!!")]
        [TestCase("!!!", "/api/")]
        [TestCase("/api/endpoint", "!!!")]
        public void StripPrefix_OnInvalidParameter_ThrowsArgumentException(string requestPath, string basePath)
            => Assert.Throws<ArgumentException>(() => UrlPath.StripPrefix(requestPath, basePath));

        [TestCase("/api/v1/endpoint", "/api/v1", "endpoint")]
        [TestCase("/api/v1/endpoint", "/api/v1/", "endpoint")]
        [TestCase("/api/v1", "/api/v1", "")]
        [TestCase("/api/v1", "/api/v1/", "")]
        [TestCase("/api/v1/endpoint", "/api/v2", null)]
        [TestCase("/api/v1/endpoint", "/api/v2/", null)]
        public void StripPrefix_ReturnsCorrectValue(string requestPath, string basePath, string? expectedResult)
            => Assert.AreEqual(expectedResult, UrlPath.StripPrefix(requestPath, basePath));

        [Test]
        public void Split_OnNullUrlPath_ThrowsArgumentNullException()
            => Assert.Throws<ArgumentNullException>(() => UrlPath.Split(null));

        [Test]
        public void Split_OnEmptyUrlPath_ThrowsArgumentException()
            => Assert.Throws<ArgumentException>(() => UrlPath.Split(""));

        [Test]
        public void Split_OnInvalidUrlPath_ThrowsArgumentException()
            => Assert.Throws<ArgumentException>(() => UrlPath.Split("does/not/start/with/slash"));

        [TestCase("/")]
        [TestCase("/api/v1/endpoint", "api", "v1", "endpoint")]
        [TestCase("///multiple///slashes//get///normalized/", "multiple", "slashes", "get", "normalized")]
        public void Split_ReturnsCorrectValues(string requestPath, params string[] segments)
            => Assert.That(UrlPath.Split(requestPath), Is.EqualTo(segments).AsCollection);
    }
}
