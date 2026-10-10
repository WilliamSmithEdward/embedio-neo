using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        [TestCase(new byte[] { 0x40 }, false)]
        [TestCase(new byte[] { 0 }, false)]
        [TestCase(new byte[] { 0, 2, 1 }, true)]
        public async Task TruncatedCapsuleUsesProtocolErrorAndKeepsSiblingStreamsHealthy(byte[] capsule, bool payload)
        {
            var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                await SendWire(wire, 1, 4, 1, CapsuleConnectBlock(), token);
                await Until(wire, 1, 1, token);
                await SendWire(wire, 0, 1, 1, capsule, token);
                while (true)
                {
                    var frame = await ReceiveWire(wire, token);
                    Assert.That(frame.Type, Is.Not.EqualTo(7), "Malformed capsule input must not close the shared connection.");
                    if (frame.Id != 1) continue;
                    Assert.That(frame.Type == 0 && (frame.Flags & 1) != 0, Is.False, "A truncated capsule must not produce successful FIN.");
                    if (frame.Type != 3) continue;
                    Assert.That(frame.Payload, Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
                    break;
                }
                await verified.Task.WaitAsync(token);
                await SendWire(wire, 1, 5, 3, RequestBlock(), token);
                Assert.That((await Until(wire, 0, 3, token)).Payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
                var nonce = new byte[] { 1, 3, 5, 7, 9, 11, 13, 15 };
                await SendWire(wire, 6, 0, 0, nonce, token);
                Assert.That((await Until(wire, 6, 0, token)).Payload, Is.EqualTo(nonce));
            }, app: async exchangeObject =>
            {
                var exchange = exchangeObject;
                if (Property<int>(exchange, "Id") != 1) { await RawEcho(exchange); return; }
                try
                {
                    var context = CreateCapsuleContext(exchange);
                    var tunnel = await context.AcceptTunnelAsync("example-tunnel", true, context.CancellationToken);
                    try
                    {
                        var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                        if (payload)
                        {
                            await channel.ReadHeaderAsync(context.CancellationToken);
                            Assert.That(await channel.ReadPayloadAsync(new byte[2], 0, 2, context.CancellationToken), Is.EqualTo(1));
                            await Assert.ThrowsAsync<EndOfStreamException>(async () => await channel.ReadPayloadAsync(new byte[2], 0, 2, context.CancellationToken));
                        }
                        else await Assert.ThrowsAsync<EndOfStreamException>(async () => await channel.ReadHeaderAsync(context.CancellationToken));
                    }
                    finally
                    {
                        await Assert.CatchAsync<OperationCanceledException>(async () => await CloseCapsuleContextAsync(context));
                    }
                    verified.TrySetResult();
                }
                catch (Exception error)
                {
                    verified.TrySetException(error);
                    throw;
                }
            });
        }

        private static IHttpTunnelContext CreateCapsuleContext(object exchange)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.MultiplexedContext", true)
                ?? throw new AssertionException("Missing multiplexed context.");
            return (IHttpTunnelContext)(Activator.CreateInstance(type, Flags, null,
                new object[] { exchange, new IPEndPoint(IPAddress.Loopback, 443), new IPEndPoint(IPAddress.Loopback, 30000), false }, null)
                ?? throw new AssertionException("Missing context constructor."));
        }
        private static Task CloseCapsuleContextAsync(IHttpTunnelContext context)
            => (Task)(context.GetType().GetMethod("CloseAsync", Flags)?.Invoke(context, null)
                ?? throw new AssertionException("Missing asynchronous context close."));

        // Literal HPACK without indexing or Huffman coding. This raw peer does
        // not use EmbedIO's encoder to generate the request field section.
        private static byte[] CapsuleConnectBlock()
        {
            using var output = new MemoryStream();
            void Field(string name, string value)
            {
                output.WriteByte(0);
                var nameBytes = Encoding.ASCII.GetBytes(name);
                output.WriteByte((byte)nameBytes.Length);
                output.Write(nameBytes, 0, nameBytes.Length);
                var valueBytes = Encoding.ASCII.GetBytes(value);
                output.WriteByte((byte)valueBytes.Length);
                output.Write(valueBytes, 0, valueBytes.Length);
            }
            Field(":method", "CONNECT");
            Field(":scheme", "http");
            Field(":authority", "localhost:443");
            Field(":path", "/tunnel");
            Field(":protocol", "example-tunnel");
            Field("capsule-protocol", "?1");
            return output.ToArray();
        }
    }
}