using System;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class WebSocketReceiveLimitsTest
    {
        [TestCase(0, 0L, 1, 0L, "maximumOutstandingMessagesPerConnection")]
        [TestCase(-1, 0L, 1, 0L, "maximumOutstandingMessagesPerConnection")]
        [TestCase(1, -1L, 1, 0L, "maximumOutstandingPayloadBytesPerConnection")]
        [TestCase(1, 0L, 0, 0L, "maximumOutstandingMessagesPerModule")]
        [TestCase(1, 0L, -1, 0L, "maximumOutstandingMessagesPerModule")]
        [TestCase(1, 0L, 1, -1L, "maximumOutstandingPayloadBytesPerModule")]
        public void ConstructorUsesOneValidationContractAndPublicParameterNames(int connectionCount, long connectionBytes,
            int moduleCount, long moduleBytes, string parameter)
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => new WebSocketReceiveLimits(connectionCount, connectionBytes, moduleCount, moduleBytes));
            Assert.That(error.ParamName, Is.EqualTo(parameter));
        }

        [Test]
        public void NullDefaultsAndIndependentLimitsAreImmutable()
        {
            var defaults = new WebSocketReceiveLimits();
            Assert.That(defaults.MaximumOutstandingMessagesPerConnection, Is.Null);
            Assert.That(defaults.MaximumOutstandingPayloadBytesPerConnection, Is.Null);
            Assert.That(defaults.MaximumOutstandingMessagesPerModule, Is.Null);
            Assert.That(defaults.MaximumOutstandingPayloadBytesPerModule, Is.Null);
            var chosen = new WebSocketReceiveLimits(2, 0, 3, long.MaxValue);
            Assert.That(chosen.MaximumOutstandingMessagesPerConnection, Is.EqualTo(2));
            Assert.That(chosen.MaximumOutstandingPayloadBytesPerConnection, Is.Zero);
            Assert.That(chosen.MaximumOutstandingMessagesPerModule, Is.EqualTo(3));
            Assert.That(chosen.MaximumOutstandingPayloadBytesPerModule, Is.EqualTo(long.MaxValue));
            foreach (var property in typeof(WebSocketReceiveLimits).GetProperties()) Assert.That(property.SetMethod, Is.Null);
        }
    }
}
