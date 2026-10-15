using System;
using System.Linq;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests.Utilities
{
    public class IPParserRangeRegressionTest
    {
        // Run these only against the iterator candidate: the original eagerly
        // materializes broad ranges before Take can limit enumeration.
        [TestCase("1.2.3.4/0", "0.0.0.0")]
        [TestCase("128.5.6.7/1", "128.0.0.0")]
        [TestCase("0.0.0.0/1", "0.0.0.0")]
        [Category("CandidateOnlyBoundedLargeRange")]
        public void BroadCidrReturnsOnlyTheRequestedPrefix(string cidr, string first)
        {
            ArgumentNullException.ThrowIfNull(first);
            var addresses = IPParser.ParseCidrNotation(cidr).Take(3).Select(x => x.ToString()).ToArray();
            var prefix = first.Substring(0, first.LastIndexOf('.') + 1);
            Assert.That(addresses, Is.EqualTo(new[] { first, prefix + "1", prefix + "2" }));
        }

        [Test]
        [Category("CandidateOnlyBoundedLargeRange")]
        public void BroadCartesianRangeCanBeConsumedIncrementally()
            => Assert.That(IPParser.TryParseSimpleIPRange("0-255.0-255.0-255.0-255").Take(3).Select(x => x.ToString()),
                Is.EqualTo(new[] { "0.0.0.0", "0.0.0.1", "0.0.0.2" }));

        [TestCase("192.0.2.5/30", "192.0.2.4,192.0.2.5,192.0.2.6,192.0.2.7")]
        [TestCase("192.0.2.5/31", "192.0.2.4,192.0.2.5")]
        [TestCase("255.255.255.255/32", "255.255.255.255")]
        public void SmallCidrPreservesNetworkBroadcastAndOrder(string cidr, string expected)
        {
            ArgumentNullException.ThrowIfNull(expected);
            var addresses = IPParser.ParseCidrNotation(cidr);
            Assert.That(addresses.Select(x => x.ToString()), Is.EqualTo(expected.Split(',')));
            Assert.That(addresses.Select(x => x.ToString()), Is.EqualTo(expected.Split(',')), "Enumeration remains repeatable.");
        }

        [Test]
        public void CartesianRangesPreserveOctetOrder()
            => Assert.That(IPParser.TryParseSimpleIPRange("192.0.1-2.254-255").Select(x => x.ToString()),
                Is.EqualTo(new[] { "192.0.1.254", "192.0.1.255", "192.0.2.254", "192.0.2.255" }));

        [TestCase(null)]
        [TestCase("bad")]
        [TestCase("192.0.2.1/33")]
        [TestCase("256.0.2.1/24")]
        public void InvalidCidrRemainsEmpty(string? cidr) => Assert.That(IPParser.ParseCidrNotation(cidr), Is.Empty);

        [TestCase("192.0.2-1.1")]
        [TestCase("192.0.2.255-0")]
        public void ReversedCartesianRangeRemainsEmpty(string range) => Assert.That(IPParser.TryParseSimpleIPRange(range), Is.Empty);
    }
}
