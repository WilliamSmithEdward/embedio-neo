using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The realm reaches the wire inside a quoted-string, so the two characters a quoted-string
    // cannot carry literally are escaped (RFC 9110, Section 5.6.4; RFC 7617, Section 2.1).
    public class BasicAuthenticationChallengeTest
    {
        [TestCase("ordinary", "Basic realm=\"ordinary\", charset=\"UTF-8\"")]
        [TestCase("say \"hello\"", "Basic realm=\"say \\\"hello\\\"\", charset=\"UTF-8\"")]
        [TestCase("back\\slash", "Basic realm=\"back\\\\slash\", charset=\"UTF-8\"")]
        [TestCase("both \"\\ here", "Basic realm=\"both \\\"\\\\ here\", charset=\"UTF-8\"")]
        public async Task TheChallengeQuotesItsRealm(string realm, string expected)
        {
            using var server = new TestWebServer();
            server.WithModule(new BasicAuthenticationModule("/", realm).WithAccount("user", "secret")).Start();

            using var response = await server.Client.GetAsync("/");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.TryGetValues(HttpHeaderNames.WWWAuthenticate, out var values), Is.True);
            Assert.That(values?.Single(), Is.EqualTo(expected));
        }
    }
}
