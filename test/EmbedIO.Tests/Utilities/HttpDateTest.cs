using System;
using System.Globalization;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests.Utilities
{
    public class HttpDateTest
    {
        [TestCase("Sun, 06 Nov 1994 08:49:37 GMT")]
        [TestCase("Sunday, 06-Nov-94 08:49:37 GMT")]
        [TestCase("Sun Nov 6 08:49:37 1994")]
        [TestCase("6 Nov 1994 8:49:37 UTC")]
        [TestCase("Sun, 06 Nov 1994 10:49:37 +02:00")]
        public void SupportedFormatsIdentifyTheSameInstant(string text)
        {
            Assert.That(HttpDate.TryParse(text, out var result), Is.True);
            Assert.That(result.ToUniversalTime(), Is.EqualTo(new DateTimeOffset(1994, 11, 6, 8, 49, 37, TimeSpan.Zero)));
        }

        [TestCase("")]
        [TestCase("not a date")]
        [TestCase("Sun, 31 Feb 1994 08:49:37 GMT")]
        public void InvalidDatesAreRejected(string text)
            => Assert.That(HttpDate.TryParse(text, out _), Is.False);

        [Test]
        public void FormattingUsesUtcAndEnglishRegardlessOfCurrentCulture()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var date = new DateTimeOffset(1994, 11, 6, 10, 49, 37, TimeSpan.FromHours(2));
                Assert.That(HttpDate.Format(date), Is.EqualTo("Sun, 06 Nov 1994 08:49:37 GMT"));
                Assert.That(HttpDate.Format(date.UtcDateTime), Is.EqualTo("Sun, 06 Nov 1994 08:49:37 GMT"));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
