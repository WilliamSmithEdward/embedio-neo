using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        private sealed class DrainOutputLifetimeStream : Stream
        {
            private readonly Stream _inner;
            internal readonly TaskCompletionSource ResponseWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource ReleaseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource RefusalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource ReleaseRefusal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationToken RefusalToken { get; private set; }
            internal DrainOutputLifetimeStream(Stream inner) { _inner = inner; }
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
                => _inner.ReadAsync(buffer, offset, count, token);
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                var response = false;
                var refusal = false;
                for (var cursor = offset; cursor + 9 <= offset + count;)
                {
                    var length = (buffer[cursor] << 16) | (buffer[cursor + 1] << 8) | buffer[cursor + 2];
                    var id = ((buffer[cursor + 5] & 127) << 24) | (buffer[cursor + 6] << 16) | (buffer[cursor + 7] << 8) | buffer[cursor + 8];
                    response |= buffer[cursor + 3] == 0 && id == 1 && (buffer[cursor + 4] & 1) != 0;
                    refusal |= buffer[cursor + 3] == 3 && id == 3;
                    cursor += 9 + length;
                }
                if (refusal)
                {
                    RefusalToken = token;
                    RefusalEntered.TrySetResult();
                    await ReleaseRefusal.Task.WaitAsync(token);
                }
                await _inner.WriteAsync(buffer, offset, count, token);
                if (response)
                {
                    ResponseWritten.TrySetResult();
                    await ReleaseResponse.Task.WaitAsync(token);
                }
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { ReleaseResponse.TrySetResult(); ReleaseRefusal.TrySetResult(); _inner.Dispose(); }
                base.Dispose(disposing);
            }
        }

        [Test]
        public async Task GracefulDrainDoesNotCancelCommittedRefusalAfterTheLastResponse()
        {
            var ready = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gateReady = new TaskCompletionSource<DrainOutputLifetimeStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            var applicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseApplication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishApplication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                var gate = await gateReady.Task.WaitAsync(token);
                try
                {
                    await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                    await applicationEntered.Task.WaitAsync(token);
                    var dispatcher = await ready.Task.WaitAsync(token);
                    await RequestDrain(dispatcher).WaitAsync(token);
                    Assert.That((await Until(wire, 7, 0, token)).Payload, Is.EqualTo(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }));
                    releaseApplication.TrySetResult();
                    await gate.ResponseWritten.Task.WaitAsync(token);
                    await SendWire(wire, 1, 5, 3, RequestBlock(), token);
                    var connection = dispatcher.GetType().GetField("_connection", Flags)?.GetValue(dispatcher)
                        ?? throw new AssertionException("Missing dispatcher connection.");
                    var transport = connection.GetType().GetField("_transport", Flags)?.GetValue(connection)
                        ?? throw new AssertionException("Missing frame transport.");
                    var outputSync = transport.GetType().GetField("_outputSync", Flags)?.GetValue(transport)
                        ?? throw new AssertionException("Missing output lock.");
                    var queue = transport.GetType().GetField("_queue", Flags)?.GetValue(transport) as ICollection
                        ?? throw new AssertionException("Missing output queue.");
                    while (true)
                    {
                        lock (outputSync) if (queue.Count != 0) break;
                        await Task.Delay(1, token);
                    }
                    gate.ReleaseResponse.TrySetResult();
                    await gate.RefusalEntered.Task.WaitAsync(token);
                    finishApplication.TrySetResult();
                    var sync = dispatcher.GetType().GetField("_sync", Flags)?.GetValue(dispatcher)
                        ?? throw new AssertionException("Missing dispatcher lock.");
                    var exchanges = dispatcher.GetType().GetField("_exchanges", Flags)?.GetValue(dispatcher) as IDictionary
                        ?? throw new AssertionException("Missing exchanges.");
                    while (true)
                    {
                        lock (sync) if (exchanges.Count == 0) break;
                        await Task.Delay(1, token);
                    }
                    Assert.That(gate.RefusalToken.IsCancellationRequested, Is.False,
                        "Finishing the last response must stop input without aborting committed shared output.");
                    gate.ReleaseRefusal.TrySetResult();
                    Assert.That((await Until(wire, 0, 1, token)).Payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
                    Assert.That((await Until(wire, 3, 3, token)).Payload, Is.EqualTo(new byte[] { 0, 0, 0, 7 }));
                    Assert.That(await wire.ReadAsync(new byte[1], token), Is.Zero);
                }
                finally { releaseApplication.TrySetResult(); finishApplication.TrySetResult(); gate.ReleaseResponse.TrySetResult(); gate.ReleaseRefusal.TrySetResult(); }
            }, app: async exchange =>
            {
                applicationEntered.TrySetResult();
                await releaseApplication.Task.WaitAsync(Property<CancellationToken>(exchange, "CancellationToken"));
                await Respond(exchange, new byte[] { 1, 2, 3 });
                await finishApplication.Task.WaitAsync(Property<CancellationToken>(exchange, "CancellationToken"));
            }, dispatcherReady: dispatcher => ready.TrySetResult(dispatcher), wrapTransport: stream =>
            {
                var gate = new DrainOutputLifetimeStream(stream); gateReady.TrySetResult(gate); return gate;
            });
        }
    }
}
