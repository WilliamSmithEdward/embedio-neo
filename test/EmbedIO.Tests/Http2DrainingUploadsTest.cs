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
        public async Task DrainingConnectionStillRejectsEvenClientStreamIds(bool headers)
        {
            var ready = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                try
                {
                    await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                    await entered.Task.WaitAsync(token);
                    await RequestDrain(await ready.Task.WaitAsync(token)).WaitAsync(token);
                    _ = await Until(wire, 7, 0, token);
                    await SendWire(wire, headers ? (byte)1 : (byte)0, headers ? (byte)5 : (byte)1,
                        2, headers ? RequestBlock() : Array.Empty<byte>(), token);
                    Assert.That((await Until(wire, 7, 0, token)).Payload,
                        Is.EqualTo(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1 }));
                }
                finally { release.TrySetResult(); }
            }, expectedConnectionError: 1, app: async exchange =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Property<CancellationToken>(exchange, "CancellationToken"));
            }, dispatcherReady: dispatcher => ready.TrySetResult(dispatcher));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InFlightRefusedUploadPreservesDrainedResponseAndConnectionCredit(bool padded)
        {
            var ready = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gateReady = new TaskCompletionSource<DrainGateStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                var gate = await gateReady.Task.WaitAsync(token);
                try
                {
                    await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                    await entered.Task.WaitAsync(token);
                    var dispatcher = await ready.Task.WaitAsync(token);
                    var drain = RequestDrain(dispatcher);
                    await gate.Entered.Task.WaitAsync(token);
                    // The client has not received GOAWAY: its successor upload is
                    // already in flight while the server starts graceful shutdown.
                    await SendWire(wire, 1, 4, 3, RequestBlock(true), token);
                    var payload = new byte[16384];
                    if (padded) payload[0] = 7;
                    await SendWire(wire, 0, padded ? (byte)8 : (byte)0, 3, payload, token);
                    await SendWire(wire, 0, padded ? (byte)9 : (byte)1, 3, payload, token);
                    gate.Release.TrySetResult();
                    await drain.WaitAsync(token);
                    Assert.That((await Until(wire, 7, 0, token)).Payload,
                        Is.EqualTo(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }));
                    Assert.That((await Until(wire, 3, 3, token)).Payload,
                        Is.EqualTo(new byte[] { 0, 0, 0, 7 }));
                    // Discarded upload bytes, including padding, still count
                    // against and replenish the shared connection flow window.
                    Assert.That((await Until(wire, 8, 0, token)).Payload,
                        Is.EqualTo(new byte[] { 0, 0, 128, 0 }));
                    release.TrySetResult();
                    Assert.That((await Until(wire, 0, 1, token)).Payload,
                        Is.EqualTo(new byte[] { 4, 5, 6 }));
                    Assert.That(calls, Is.EqualTo(1), "Refused upload must not invoke the application.");
                }
                finally { gate.Release.TrySetResult(); release.TrySetResult(); }
            }, app: async exchange =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(Property<CancellationToken>(exchange, "CancellationToken"));
                await Respond(exchange, new byte[] { 4, 5, 6 });
            }, dispatcherReady: dispatcher => ready.TrySetResult(dispatcher), wrapTransport: stream =>
            {
                var gate = new DrainGateStream(stream);
                gateReady.TrySetResult(gate);
                return gate;
            });
        }
    }
}
