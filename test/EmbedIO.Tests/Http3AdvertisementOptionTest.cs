using System;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The public switch for combined hosting's Alt-Svc header (#525). The header itself is
    // tested against real listeners in Http3ListenerTest.AltSvc.
    [TestFixture]
    public class Http3AdvertisementOptionTest
    {
        [Test]
        public void CombinedHostingAdvertisesHttp3ByDefault() =>
            Assert.That(new WebServerOptions().AdvertiseHttp3, Is.True);

        [Test]
        public void WithoutHttp3AdvertisementTurnsItOff() =>
            Assert.That(new WebServerOptions().WithoutHttp3Advertisement().AdvertiseHttp3, Is.False);

        [Test]
        public void TheSettingLocksWithTheServersOptions()
        {
            var options = new WebServerOptions().WithUrlPrefix("http://localhost:9696/").WithMode(HttpListenerMode.EmbedIO);
            using var server = new WebServer(options);

            using (Assert.EnterMultipleScope())
            {
                Assert.Throws<InvalidOperationException>(() => options.AdvertiseHttp3 = false);
                Assert.Throws<InvalidOperationException>(() => options.WithoutHttp3Advertisement());
                Assert.That(options.AdvertiseHttp3, Is.True);
            }
        }
    }
}
