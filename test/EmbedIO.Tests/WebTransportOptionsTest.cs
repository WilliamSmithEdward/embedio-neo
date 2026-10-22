using System;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The public WebTransport settings. Sessions themselves need a peer with QUIC
    // datagrams and reliable reset; test/EmbedIO.Quic drives them with quic-go.
    [TestFixture]
    public class WebTransportOptionsTest
    {
        [Test]
        public void TheDefaultsAdmitOneSessionAndTheBrowserDraft()
        {
            var options = new WebTransportOptions();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(options.MaximumSessionsPerConnection, Is.EqualTo(1));
                Assert.That(options.MaximumIncomingStreamsPerSession, Is.EqualTo(100));
                Assert.That(options.AcceptBrowserDraft02, Is.True);
            }
        }

        [Test]
        public void WebTransportIsOffUntilEnabled() => Assert.That(new WebServerOptions().WebTransport, Is.Null);

        [TestCase(1)]
        [TestCase(64)]
        public void SessionLimitsInsideTheRangeAreKept(int value) =>
            Assert.That(
                new WebTransportOptions { MaximumSessionsPerConnection = value }.MaximumSessionsPerConnection,
                Is.EqualTo(value)
            );

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(65)]
        public void SessionLimitsOutsideTheRangeAreRefused(int value) =>
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _ = new WebTransportOptions { MaximumSessionsPerConnection = value }
            );

        [TestCase(1)]
        [TestCase(1024)]
        public void StreamLimitsInsideTheRangeAreKept(int value) =>
            Assert.That(
                new WebTransportOptions { MaximumIncomingStreamsPerSession = value }.MaximumIncomingStreamsPerSession,
                Is.EqualTo(value)
            );

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(1025)]
        public void StreamLimitsOutsideTheRangeAreRefused(int value) =>
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _ = new WebTransportOptions { MaximumIncomingStreamsPerSession = value }
            );

        [Test]
        public void WithWebTransportInstallsNewSettingsChangedByTheCallback()
        {
            var calls = 0;
            var options = new WebServerOptions().WithWebTransport(webTransport =>
            {
                calls++;
                webTransport.MaximumSessionsPerConnection = 4;
                webTransport.MaximumIncomingStreamsPerSession = 9;
                webTransport.AcceptBrowserDraft02 = false;
            });
            var installed = options.WebTransport
                ?? throw new AssertionException("WithWebTransport installed no settings.");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(installed.MaximumSessionsPerConnection, Is.EqualTo(4));
                Assert.That(installed.MaximumIncomingStreamsPerSession, Is.EqualTo(9));
                Assert.That(installed.AcceptBrowserDraft02, Is.False);
            }

            Assert.That(
                new WebServerOptions().WithWebTransport().WebTransport?.MaximumSessionsPerConnection,
                Is.EqualTo(1)
            );
        }

        [Test]
        public void TcpOnlyServersRefuseWebTransport()
        {
            var options = new WebServerOptions()
                .WithUrlPrefix("http://localhost:9696/")
                .WithMode(HttpListenerMode.EmbedIO)
                .WithWebTransport();
            var settings = options.WebTransport ?? throw new AssertionException("Missing settings.");
            var refusal = Assert.Throws<NotSupportedException>(() => _ = new WebServer(options));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    refusal?.Message,
                    Does
                        .Contain("EmbedIOHttp3")
                        .And.Contain("EmbedIOCombined")
                );
                // The server locked the settings before refusing them.
                Assert.Throws<InvalidOperationException>(() => settings.MaximumSessionsPerConnection = 2);
                Assert.Throws<InvalidOperationException>(() => options.WebTransport = null);
            }
        }
    }
}
