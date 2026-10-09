using System;
using System.Linq;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async System.Threading.Tasks.Task DiscardedClosedStreamDataReturnsConnectionCreditAndPreservesOtherRequests(bool peerReset)
        {
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                await SendWire(wire, 1, peerReset ? (byte)4 : (byte)5, 1, RequestBlock(peerReset), token);
                if (peerReset)
                {
                    await SendWire(wire, 3, 0, 1, new byte[] { 0, 0, 0, 8 }, token);
                    await SendWire(wire, 6, 0, 0, new byte[8], token);
                    Assert.That((await Until(wire, 6, 0, token)).Flags & 1, Is.EqualTo(1));
                }
                else
                {
                    var ended = false;
                    while (!ended)
                    {
                        var response = await ReceiveWire(wire, token);
                        Assert.That(response.Type, Is.Not.EqualTo(7));
                        Assert.That(response.Type, Is.Not.EqualTo(3));
                        ended = response.Id == 1 && (response.Type == 0 || response.Type == 1) && (response.Flags & 1) != 0;
                    }
                }

                // RFC 9113 permits minimal processing on closed streams. Every
                // discarded byte still consumes and returns connection credit.
                for (var round = 0; round < 2; round++)
                {
                    await SendWire(wire, 0, 0, 1, new byte[16384], token);
                    await SendWire(wire, 0, 0, 1, new byte[16384], token);
                    var credit = await Until(wire, 8, 0, token);
                    Assert.That(credit.Payload, Is.EqualTo(new byte[] { 0, 0, 128, 0 }));
                }
                await SendWire(wire, 1, 5, 3, RequestBlock(), token);
                Assert.That((await Until(wire, 0, 3, token)).Payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
        }
    }
}
