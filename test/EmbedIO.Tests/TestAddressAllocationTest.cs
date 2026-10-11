using System;
using System.Linq;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Regression coverage for the hosted Windows 503: HTTP.sys answers every request on a
    // port that carries a strong-wildcard URL reservation (http://+:port/) before an
    // explicit localhost registration is consulted, so the shared test address allocator
    // must never hand out such a port.
    [NonParallelizable]
    public class TestAddressAllocationTest
    {
        [Test]
        public void ParseKeepsOnlyWildcardReservations()
        {
            const string output = "URL Reservations:\r\n-----------------\r\n\r\n" +
                "    Reserved URL            : http://+:12292/ \r\n        User: NT AUTHORITY\\SYSTEM\r\n" +
                "    Reservierte URL         : https://*:8443/api/ \r\n" +
                "    Reserved URL            : http://localhost:5000/ \r\n" +
                "    Reserved URL            : http://127.0.0.1:6000/ \r\n" +
                "    Reserved URL            : http://[::1]:6001/ \r\n" +
                "    Reserved URL            : HTTP://+:47001/wsman/ \r\n";
            Assert.That(HttpSysReservedPorts.Parse(output), Is.EquivalentTo(new[] { 12292, 8443, 47001 }));
        }

        [TestCase("http://+:0/")]
        [TestCase("http://+:65536/")]
        [TestCase("http://+:abc/")]
        [TestCase("http://+/")]
        [TestCase("")]
        public void ParseIgnoresInvalidPorts(string output)
            => Assert.That(HttpSysReservedPorts.Parse(output), Is.Empty);

        [Test]
        public void AllocatorSkipsExcludedPorts()
        {
            var first = new Uri(Resources.GetServerAddress()).Port;
            Resources.Exclude(first + 1);
            Resources.Exclude(first + 2);
            Assert.That(new Uri(Resources.GetServerAddress()).Port, Is.EqualTo(first + 3));
            Assert.That(new Uri(Resources.GetServerAddress()).Port, Is.EqualTo(first + 4));
        }

        [Test]
        public void AllocatedAddressesAvoidWildcardReservedPorts()
        {
            var reserved = HttpSysReservedPorts.Current;
            TestContext.Out.WriteLine($"Wildcard-reserved HTTP.sys ports on this machine: {string.Join(", ", reserved.OrderBy(p => p))}");
            foreach (var port in reserved) Assert.That(Resources.IsExcluded(port), Is.True, $"port {port}");
            var allocated = Enumerable.Range(0, 20).Select(_ => new Uri(Resources.GetServerAddress()).Port).ToArray();
            Assert.That(allocated.Intersect(reserved), Is.Empty);
            Assert.That(allocated, Is.Unique);
        }
    }
}
