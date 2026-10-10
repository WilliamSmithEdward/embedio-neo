using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ThrowingApplicationCancellationCallbackDoesNotStrandCompletedStreamShutdown(bool reset)
        {
            var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration callback = default;
            try
            {
                await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
                {
                    await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                    _ = await Until(wire, reset ? (byte)3 : (byte)0, 1, token);
                    await callbackRan.Task.WaitAsync(token);
                    var nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                    await SendWire(wire, 6, 0, 0, nonce, token);
                    Assert.That((await Until(wire, 6, 0, token)).Payload, Is.EqualTo(nonce),
                        "A completed application's callback failure must not prevent sibling connection traffic.");
                }, app: async exchange =>
                {
                    var cancellation = (CancellationToken)(exchange.GetType().GetProperty("CancellationToken")?.GetValue(exchange)
                        ?? throw new AssertionException("Missing exchange cancellation token."));
                    callback = cancellation.Register(() =>
                    {
                        callbackRan.TrySetResult();
                        throw new InvalidOperationException("Injected application cancellation callback failure.");
                    });
                    if (reset) throw new System.IO.IOException("Injected application failure before response.");
                    await RawEcho(exchange);
                });
            }
            finally { callback.Dispose(); }
        }
    }
}
