using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class ListenerModeCompatibilityTest
    {
        [TestCase(HttpListenerMode.EmbedIO, 0)]
        [TestCase(HttpListenerMode.EmbedIOHttp3, 2)]
        [TestCase(HttpListenerMode.EmbedIOCombined, 3)]
        public void PersistedSupportedModeIdentifiersRemainStable(HttpListenerMode mode, int identifier)
            => Assert.That((int)mode, Is.EqualTo(identifier));
    }
}
