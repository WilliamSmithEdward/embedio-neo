using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class MimeRangeAuditTest
    {
        [TestCase("application/json", "application/*json", false)]
        [TestCase("application/*json", "application/*json", true)]
        [TestCase("application/json", "*application/json", false)]
        [TestCase("*application/json", "*application/json", true)]
        [TestCase("application/json", "application/*", true)]
        [TestCase("text/plain", "application/*", false)]
        [TestCase("text/plain", "*/*", true)]
        [TestCase("Application/json", "application/json", false)]
        public void OnlyEntireTokensAreWildcards(string mimeType, string range, bool expected)
            => Assert.That(MimeType.IsInRange(mimeType, range), Is.EqualTo(expected));
    }
}
