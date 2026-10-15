using System.Collections.Generic;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class QValueHeaderOrderTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public void EqualQualityPreferencePreservesOrderAcrossHeaderValues(bool iterator)
        {
            var combined = new QValueList(false, "deflate,gzip");
            var separated = new QValueList(false, Values(iterator, "deflate", "gzip"));
            var candidates = new[] { "gzip", "deflate" };
            Assert.That(combined.FindPreferred(candidates), Is.EqualTo("deflate"));
            Assert.That(separated.FindPreferred(candidates), Is.EqualTo(combined.FindPreferred(candidates)),
                "Splitting a comma-separated header must not reverse the established client-order tie break.");
            Assert.That(separated.QValues["gzip"].Ordinal, Is.GreaterThan(separated.QValues["deflate"].Ordinal));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompressionTiePreservesClientOrderAcrossHeaderValues(bool iterator)
        {
            var combined = new QValueList(true, "deflate,gzip");
            var separated = new QValueList(true, Values(iterator, "deflate", "gzip"));
            Assert.That(combined.TryNegotiateContentEncoding(true, out var expected, out _), Is.True);
            Assert.That(expected, Is.EqualTo(CompressionMethod.Deflate));
            Assert.That(separated.TryNegotiateContentEncoding(true, out var actual, out _), Is.True);
            Assert.That(actual, Is.EqualTo(expected),
                "Neither candidate is Brotli; the documented client-order rule decides this tie.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void QualityWeightsAndRejectedEntriesStillTakePrecedence(bool iterator)
        {
            var list = new QValueList(false, Values(iterator, null, "", "deflate;q=0.5,ignored;q=0", null, "gzip;q=0.9"));
            Assert.That(list.QValues["deflate"], Is.EqualTo((500, 0)));
            Assert.That(list.QValues["ignored"], Is.EqualTo((0, 1)));
            Assert.That(list.QValues["gzip"], Is.EqualTo((900, 2)));
            Assert.That(list.FindPreferred(new[] { "ignored", "deflate", "gzip" }), Is.EqualTo("gzip"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateNamesKeepTheirLastWeightAndPosition(bool iterator)
        {
            var list = new QValueList(true, Values(iterator, "deflate;q=0.9", "gzip;q=0.5,DEFLATE;q=0.5,*;q=0"));
            Assert.That(list.QValues["deflate"], Is.EqualTo((500, 2)));
            Assert.That(list.QValues["gzip"], Is.EqualTo((500, 1)));
            Assert.That(list.FindPreferred(new[] { "deflate", "gzip" }), Is.EqualTo("gzip"));
            Assert.That(list.IsCandidate("unknown"), Is.False);
        }

        private static IEnumerable<string?> Values(bool iterator, params string?[] values)
            => iterator ? Enumerate(values) : values;

        private static IEnumerable<string?> Enumerate(string?[] values)
        {
            foreach (var value in values) yield return value;
        }
    }
}
