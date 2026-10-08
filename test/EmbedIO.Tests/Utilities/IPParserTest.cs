using EmbedIO.Utilities;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace EmbedIO.Tests.Utilities
{
    public class IPParserTest
    {
        [Test]
        public async Task TrailingDotHostnameFollowsPlatformDns()
        {
            const string Address = "192.168.152.1.";
            IPAddress[] expected;
            try
            {
                expected = await Dns.GetHostAddressesAsync(Address);
            }
            catch (SocketException)
            {
                expected = System.Array.Empty<IPAddress>();
            }

            Assert.That(await IPParser.ParseAsync(Address), Is.EquivalentTo(expected));
        }
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("192.168.1.52", false)]
        [TestCase("192.168.1.52/", false)]
        [TestCase("192.168.1.52-2", false)]
        [TestCase("192.168.1.52/48", false)]
        [TestCase("192.168.152/48", false)]
        [TestCase("192.168.1.52/256", false)]
        [TestCase("192.168.1.52/24.1", false)]
        [TestCase("192.168.1.52/24", true)]
        public void IsCIDRNotation_ReturnsCorrectValue(string? address, bool expectedResult)
            => Assert.AreEqual(expectedResult, IPParser.IsCidrNotation(address));

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("192.168.152", false)]
        [TestCase("192.168.152.1.", false)]
        [TestCase("192.168.1.52-", false)]
        [TestCase("192.168.1-.52", false)]
        [TestCase("192.168.1-2.52/1", false)]
        [TestCase("192.168.1-2.52-1", true)]
        [TestCase("192.168-169.1-2.52-53", true)]
        [TestCase("192-193.168-169.1-2.52-53", true)]
        public void IsSimpleIPRange_ReturnsCorrectValue(string? address, bool expectedResult)
            => Assert.AreEqual(expectedResult, IPParser.IsSimpleIPRange(address));

        [TestCase(null)]
        [TestCase("192.168.1.52/")]
        [TestCase("192.168.1.52-2")]
        [TestCase("192.168.1.52/48")]
        [TestCase("192.168.152/24")]
        [TestCase("192.168.1.52/256")]
        [TestCase("192.168.1.52/24.1")]
        [TestCase("192.168.1.52-")]
        [TestCase("192.168.1-.52")]
        [TestCase("192.168.1-2.52/1")]
        [TestCase("192.168.1-2/3.52/1")]
        [TestCase("192.168.1-x.52/1")]
        [TestCase("192.168.1-2.52-1")]
        [TestCase("192.168.2-1.52-1")]
        public async Task IpParseEmpty_ReturnsCorrectValue(string? address)
            => Assert.That(await IPParser.ParseAsync(address), Is.Empty);

        [TestCase("")]
        [TestCase("192")]
        [TestCase("192.168")]
        [TestCase("192.168.152")]
        [TestCase("192.168.1.52/24")]
        [TestCase("192.168-169.1-2.52-53")]
        [TestCase("192-193.168-169.1-2.52-53")]
        public async Task IpParseNotEmpty_ReturnsCorrectValue(string? address)
            => Assert.That(await IPParser.ParseAsync(address), Is.Not.Empty);

        [TestCase("192", 1)]
        [TestCase("192.168", 1)]
        [TestCase("192.168.152", 1)]
        [TestCase("192.168.1.1/24", 256)]
        [TestCase("192.168.1.50-53", 4)]
        [TestCase("192.168.1-2.50-53", 8)]
        [TestCase("192.168-169.1-2.50-53", 16)]
        [TestCase("192-193.168-169.1-2.50-53", 32)]
        public async Task IpParseCount_ReturnsCorrectValue(string? address, int count)
            => Assert.AreEqual(count, (await IPParser.ParseAsync(address)).Count());
    }
}
