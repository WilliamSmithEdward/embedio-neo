using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests.Utilities
{
    public class QValueListTest
    {
        [TestCase("0", 0)]
        [TestCase("0.1", 100)]
        [TestCase("0.12", 120)]
        [TestCase("0.123", 123)]
        [TestCase("1.000", 1000)]
        public void QualityPrecisionIsNormalized(string quality, int expected)
        {
            var list = new QValueList(false, $"gzip;q={quality}");
            Assert.That(list.TryGetWeight("gzip", out var weight), Is.True);
            Assert.That(weight, Is.EqualTo(expected));
            Assert.That(list.IsCandidate("gzip"), Is.EqualTo(expected > 0));
        }

        [Test]
        public void ExplicitRejectionOverridesWildcardAndWildcardCanBeDisabled()
        {
            var list = new QValueList(true, "*;q=0.5,gzip;q=0");
            Assert.That(list.IsCandidate("gzip"), Is.False);
            Assert.That(list.TryGetWeight("br", out var weight), Is.True);
            Assert.That(weight, Is.EqualTo(500));
            Assert.That(new QValueList(false, "*;q=0.5").IsCandidate("br"), Is.False);
        }

        [TestCase("gzip;q=0.5,br;q=0.9", "br", 0)]
        [TestCase("gzip;q=0.5,br;q=0.5", "gzip", 1)]
        [TestCase("gzip;q=0,br;q=0", null, -1)]
        public void PreferenceUsesQualityThenClientOrder(string header, string? expected, int index)
        {
            var list = new QValueList(false, header);
            Assert.That(list.FindPreferred(new[] { "br", "gzip" }), Is.EqualTo(expected));
            Assert.That(list.FindPreferredIndex("br", "gzip"), Is.EqualTo(index));
        }

        [Test]
        public void MediaParametersArePreservedAndAcceptExtensionsDiscarded()
        {
            var list = new QValueList(false, "text/html;level=1;q=0.7;extension=yes");
            Assert.That(list.TryGetWeight("text/html;level=1", out var weight), Is.True);
            Assert.That(weight, Is.EqualTo(700));
            Assert.That(list.IsCandidate("text/html"), Is.False);
        }
    }
}
