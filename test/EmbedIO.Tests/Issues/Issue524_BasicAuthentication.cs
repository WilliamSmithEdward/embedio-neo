using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue524_BasicAuthentication
    {
        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task ChallengesAndCredentialsWorkOnRealListeners(HttpListenerMode mode, bool layered)
            => Assert.That(await PlatformTests.BasicAuthenticationSmoke.RunAsync(mode, Resources.GetServerAddress(), layered), Is.EqualTo(10));
    }
}
