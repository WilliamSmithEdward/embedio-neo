using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        // DATA for a request body the application already released concerns that stream alone.
        // It must not end the connection that other streams share.
        [TestCase(false)]
        [TestCase(true)]
        public async Task DataForAReleasedRequestBodyKeepsTheConnection(bool trailers)
        {
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                try
                {
                    await SendWire(wire, 1, 4, 1, RequestBlock(true), token);
                    await released.Task.WaitAsync(token);
                    await SendWire(wire, 0, 0, 1, new byte[10], token);
                    if (trailers) await SendWire(wire, 1, 5, 1, new byte[] { 0x40, 1, (byte)'x', 1, (byte)'y' }, token);
                    await SendWire(wire, 1, 5, 3, RequestBlock(), token);
                    Assert.That((await Until(wire, 0, 3, token)).Payload, Is.EqualTo(new byte[] { 2 }));
                    release.TrySetResult();
                    Assert.That((await Until(wire, 0, 1, token)).Payload, Is.EqualTo(new byte[] { 1 }));
                }
                finally { release.TrySetResult(); }
            }, app: async exchange =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    var body = exchange.GetType().GetProperty("Body", Flags)?.GetValue(exchange) as IDisposable
                        ?? throw new AssertionException("Missing request body.");
                    body.Dispose();
                    released.TrySetResult();
                    await release.Task.WaitAsync(Property<CancellationToken>(exchange, "CancellationToken"));
                    await Respond(exchange, new byte[] { 1 });
                }
                else
                {
                    await Respond(exchange, new byte[] { 2 });
                }
            });
        }
    }
}
