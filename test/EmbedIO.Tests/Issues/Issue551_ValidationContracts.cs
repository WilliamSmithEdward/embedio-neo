using System;
using System.IO;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue551_ValidationContracts
    {
        [Test]
        public void NotNullReturnsOriginalInterfacesAndDelegatesWithoutWrappers()
        {
            using var stream = new MemoryStream();
            IDisposable? service = stream;
            var count = 0;
            EventHandler? callback = (_, _) => count++;
            IDisposable checkedService = Validate.NotNull(nameof(service), service);
            EventHandler checkedCallback = Validate.NotNull(nameof(callback), callback);
            Assert.That(checkedService, Is.SameAs(service));
            Assert.That(checkedCallback, Is.SameAs(callback));
            checkedCallback(this, EventArgs.Empty);
            Assert.That(count, Is.EqualTo(1));
        }

        [TestCase("reference")]
        [TestCase("string")]
        [TestCase("local-path")]
        [TestCase("url")]
        [TestCase("url-path")]
        [TestCase("route")]
        [TestCase("token")]
        [TestCase("mime")]
        public void RequiredNullInputsRetainArgumentNullExceptionAndCallerName(string kind)
        {
            const string callerName = "reportedArgument";
            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                _ = kind switch
                {
                    "reference" => Validate.NotNull<object>(callerName, null),
                    "string" => Validate.NotNullOrEmpty(callerName, null),
                    "local-path" => Validate.LocalPath(callerName, null, false),
                    "url" => Validate.Url(callerName, null),
                    "url-path" => Validate.RoutePath(callerName, null, false),
                    "route" => Validate.Route(callerName, null, false),
                    "token" => Validate.Rfc2616Token(callerName, null),
                    "mime" => Validate.MimeType(callerName, null, false),
                    _ => throw new InvalidOperationException(kind),
                };
            });
            Assert.That(exception.ParamName, Is.EqualTo(callerName));
        }

        [TestCase(" ")]
        [TestCase("\t")]
        [TestCase("\u2003")]
        public void NotNullOrEmptyRetainsWhitespaceRatherThanStrengtheningPolicy(string input)
            => Assert.That(Validate.NotNullOrEmpty(nameof(input), input), Is.SameAs(input));

        [TestCase("")]
        [TestCase("\t")]
        [TestCase("\0")]
        [TestCase("*")]
        [TestCase("?")]
        public void LocalPathRejectsEmptyWhitespaceAndPortableInvalidCharacters(string input)
        {
            var exception = Assert.Throws<ArgumentException>(() => Validate.LocalPath(nameof(input), input, false));
            Assert.That(exception.ParamName, Is.EqualTo(nameof(input)));
        }

        [Test]
        public void LocalPathReturnsDerivedValueWithoutChangingInputOrRequiringExistence()
        {
            var unique = "validation-551-" + Guid.NewGuid().ToString("N");
            var input = Path.Combine("unused-551", "..", unique);
            var original = input;
            Assert.That(Validate.LocalPath(nameof(input), input, false), Is.SameAs(input));
            var fullPath = Validate.LocalPath(nameof(input), input, true);
            Assert.That(input, Is.EqualTo(original));
            Assert.That(Path.IsPathFullyQualified(fullPath), Is.True);
            Assert.That(Path.GetFileName(fullPath), Is.EqualTo(unique));
            Assert.That(Directory.Exists(fullPath), Is.False);
        }

        [TestCase(true, "/api/items/")]
        [TestCase(false, "/api/items")]
        public void UrlPathNormalizesReturnedValueWithoutMutatingCaller(bool basePath, string expected)
        {
            const string input = "/api//items///";
            var normalized = Validate.RoutePath(nameof(input), input, basePath);
            Assert.That(normalized, Is.EqualTo(expected));
            Assert.That(input, Is.EqualTo("/api//items///"));
            Assert.That(Validate.RoutePath(nameof(input), "/a%2Fb//", false), Is.EqualTo("/a%2Fb"));
        }

        [Test]
        public void DomainChecksRetainTokenAndMediaRangePolicies()
        {
            const string protocol = "json-v1";
            Assert.That(Validate.Rfc2616Token(nameof(protocol), protocol), Is.SameAs(protocol));
            var tokenFailure = Assert.Throws<ArgumentException>(() => Validate.Rfc2616Token(nameof(protocol), "json\r\nforged"));
            Assert.That(tokenFailure.ParamName, Is.EqualTo(nameof(protocol)));
            const string media = "*/*";
            Assert.That(Validate.MimeType(nameof(media), media, true), Is.SameAs(media));
            var mediaFailure = Assert.Throws<ArgumentException>(() => Validate.MimeType(nameof(media), media, false));
            Assert.That(mediaFailure.ParamName, Is.EqualTo(nameof(media)));
        }
    }
}
