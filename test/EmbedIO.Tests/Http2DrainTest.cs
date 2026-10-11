using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        private static Task RequestDrain(object dispatcher)
            => (Task)(dispatcher.GetType().GetMethod("DrainAsync", Flags)?.Invoke(dispatcher, null)
                ?? throw new AssertionException("Missing HTTP/2 drain operation."));

        private sealed class DrainGateStream : Stream
        {
            private readonly Stream _inner;
            private readonly byte _frameType;
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal DrainGateStream(Stream inner, byte frameType = 7) { _inner = inner; _frameType = frameType; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => _inner.ReadAsync(buffer, offset, count, token);
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (count >= 9 && buffer[offset + 3] == _frameType)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(token);
                }
                await _inner.WriteAsync(buffer, offset, count, token);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Release.TrySetResult(); _inner.Dispose(); }
                base.Dispose(disposing);
            }
        }

        [Test]
        public async Task ShortStringResponseReturnsWhileTransportWriteIsPending()
        {
            var gateReady = new TaskCompletionSource<DrainGateStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                var gate = await gateReady.Task.WaitAsync(token);
                try
                {
                    await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                    await gate.Entered.Task.WaitAsync(token);
                    var sending = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                    Assert.That(sending.IsCompleted, Is.False, "A blocked network write must leave an asynchronous response pending.");
                }
                finally { gate.Release.TrySetResult(); }
                Assert.That((await Until(wire, 0, 1, token)).Payload, Is.EqualTo(new byte[] { 97, 98, 99 }));
            }, app: async exchange =>
            {
                var context = Adapter(exchange);
                try
                {
                    var sending = context.SendStringAsync("abc", "text/plain", WebServer.Utf8NoBomEncoding);
                    returned.TrySetResult(sending);
                    await sending;
                }
                finally { context.Close(); }
            }, wrapTransport: stream =>
            {
                var gate = new DrainGateStream(stream, 1);
                gateReady.TrySetResult(gate);
                return gate;
            });
        }

        [Test]
        public async Task ExternalDrainCallersSharePendingGoawayWrite()
        {
            var ready = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gateReady = new TaskCompletionSource<DrainGateStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                await SendWire(wire, 6, 0, 0, new byte[8], token);
                _ = await Until(wire, 6, 0, token);
                var dispatcher = await ready.Task.WaitAsync(token);
                var gate = await gateReady.Task.WaitAsync(token);
                var drain = RequestDrain(dispatcher);
                try
                {
                    await gate.Entered.Task.WaitAsync(token);
                    Assert.That(drain.IsCompleted, Is.False);
                    Assert.That(RequestDrain(dispatcher), Is.SameAs(drain));
                }
                finally { gate.Release.TrySetResult(); }
                await drain.WaitAsync(token);
                Assert.That((await Until(wire, 7, 0, token)).Payload, Is.EqualTo(new byte[8]));
                Assert.That(await wire.ReadAsync(new byte[1], token), Is.Zero);
            }, dispatcherReady: dispatcher => ready.TrySetResult(dispatcher), wrapTransport: stream =>
            {
                var gate = new DrainGateStream(stream);
                gateReady.TrySetResult(gate);
                return gate;
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExternalDrainClosesIdleConnection(bool concurrent)
        {
            var ready = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                // Complete the SETTINGS acknowledgment before testing an idle
                // close; otherwise unread fixture bytes can cause a TCP reset.
                await SendWire(wire, 6, 0, 0, new byte[8], token);
                _ = await Until(wire, 6, 0, token);
                var dispatcher = await ready.Task.WaitAsync(token);
                var drain = RequestDrain(dispatcher);
                if (concurrent) Assert.That(RequestDrain(dispatcher), Is.SameAs(drain));
                await drain.WaitAsync(token);
                var goaway = await Until(wire, 7, 0, token);
                Assert.That(goaway.Payload, Is.EqualTo(new byte[8]));
                var end = new byte[1];
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                Assert.That(await wire.ReadAsync(end, deadline.Token), Is.Zero, "Idle drain must close without client shutdown.");
            }, dispatcherReady: dispatcher => ready.TrySetResult(dispatcher));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ExternalDrainPreservesAcceptedStreamAndRefusesSuccessor(bool concurrent)
        {
            var ready = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                try
                {
                    await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                    await entered.Task.WaitAsync(token);
                    var dispatcher = await ready.Task.WaitAsync(token);
                    var drain = RequestDrain(dispatcher);
                    if (concurrent) Assert.That(RequestDrain(dispatcher), Is.SameAs(drain));
                    await drain.WaitAsync(token);
                    Assert.That((await Until(wire, 7, 0, token)).Payload, Is.EqualTo(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }));
                    await SendWire(wire, 1, 5, 3, RequestBlock(), token);
                    Assert.That((await Until(wire, 3, 3, token)).Payload, Is.EqualTo(new byte[] { 0, 0, 0, 7 }));
                    release.TrySetResult();
                    Assert.That((await Until(wire, 0, 1, token)).Payload, Is.EqualTo(new byte[] { 4, 5, 6 }));
                    // Consume the final END_STREAM if it is a separate empty DATA frame.
                    while (true)
                    {
                        var header = new byte[9];
                        var first = await wire.ReadAsync(header.AsMemory(0, 1), token);
                        if (first == 0) break;
                        await wire.ReadExactlyAsync(header.AsMemory(1), token);
                        var payload = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
                        await wire.ReadExactlyAsync(payload, token);
                        Assert.That(header[3], Is.Not.EqualTo(7), "Concurrent drain must not emit another GOAWAY.");
                    }
                    Assert.That(calls, Is.EqualTo(1));
                }
                finally { release.TrySetResult(); }
            }, app: async exchange =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(Property<CancellationToken>(exchange, "CancellationToken"));
                await Respond(exchange, new byte[] { 4, 5, 6 });
            }, dispatcherReady: dispatcher => ready.TrySetResult(dispatcher));
        }
    }
}
