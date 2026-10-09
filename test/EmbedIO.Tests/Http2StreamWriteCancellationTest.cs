using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2FrameTest
    {
        private static readonly Type ConnectionType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2Connection", true)
            ?? throw new AssertionException("Missing HTTP/2 connection.");

        private static object Connection(Stream stream, Action<Exception> failed)
        {
            var connection = Activator.CreateInstance(ConnectionType, Flags, null, new object[] { stream }, null)
                ?? throw new AssertionException("Missing connection constructor.");
            (ConnectionType.GetProperty("OutputFailed", Flags) ?? throw new AssertionException("Missing output failure callback."))
                .SetValue(connection, failed);
            return connection;
        }

        private static async Task SendRequest(object connection, CancellationToken token, params object[] frames)
        {
            var reservations = new Dictionary<int, int>();
            foreach (var frame in frames)
                if (Property<byte>(frame, "Type") == 0 && Property<byte[]>(frame, "Payload").Length > 0)
                {
                    var id = Property<int>(frame, "StreamId");
                    reservations.TryGetValue(id, out var previous);
                    reservations[id] = previous + Property<byte[]>(frame, "Payload").Length;
                }
            var flow = ConnectionType.GetProperty("SendFlow", Flags)?.GetValue(connection)
                ?? throw new AssertionException("Missing send flow control.");
            foreach (var reservation in reservations)
            {
                (flow.GetType().GetMethod("Open", Flags) ?? throw new AssertionException("Missing stream window registration."))
                    .Invoke(flow, new object[] { reservation.Key });
                var credit = (Task<int>)(flow.GetType().GetMethod("ReserveAsync", Flags)?.Invoke(flow,
                    new object[] { reservation.Key, reservation.Value, CancellationToken.None })
                    ?? throw new AssertionException("Missing reservation task."));
                Assert.That(await credit, Is.EqualTo(reservation.Value));
            }
            var array = Array.CreateInstance(FrameType, frames.Length);
            for (var i = 0; i < frames.Length; i++) array.SetValue(frames[i], i);
            // The old connection only offered SendAsync, which applied the stream
            // token to shared transport I/O. Retain that path for the baseline.
            var send = ConnectionType.GetMethod("SendStreamAsync", Flags) ?? ConnectionType.GetMethod("SendAsync", Flags)
                ?? throw new AssertionException("Missing connection writer.");
            await (Task)(send.Invoke(connection, new object[] { array, token }) ?? throw new AssertionException("Missing send task."));
        }

        [Test]
        public async Task StreamResetAfterAFrameStartsDoesNotInterruptSharedOutput()
        {
            using var source = new GatedOutput();
            var failures = 0;
            using var connection = (IDisposable)Connection(source, _ => Interlocked.Increment(ref failures));
            using var reset = new CancellationTokenSource();
            var first = SendRequest(connection, reset.Token,
                Frame(0, 0, 1, new byte[] { 10, 20, 30 }), Frame(0, 1, 1, new byte[] { 40 }));
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            reset.Cancel();
            source.Release.TrySetResult(true);
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await SendRequest(connection, CancellationToken.None, Frame(6, 1, 0, new byte[8]));
            Assert.That(source.ToArray().Length, Is.EqualTo(12 + 10 + 17));
            Assert.That(failures, Is.Zero);
        }

        [Test]
        public async Task ResetWhileWaitingForSharedOutputDoesNotReportConnectionFailure()
        {
            using var source = new GatedOutput();
            var failures = 0;
            using var connection = (IDisposable)Connection(source, _ => Interlocked.Increment(ref failures));
            var first = SendRequest(connection, CancellationToken.None, Frame(0, 1, 1, new byte[] { 10 }));
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var reset = new CancellationTokenSource();
            var queued = SendRequest(connection, reset.Token, Frame(0, 1, 3, new byte[] { 20 }));
            try
            {
                reset.Cancel();
                await Assert.ThatAsync(async () => await queued.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>());
                Assert.That(failures, Is.Zero, "An unstarted stream write cannot corrupt the connection.");
            }
            finally
            {
                source.Release.TrySetResult(true);
                await first.WaitAsync(TimeSpan.FromSeconds(2));
            }
            await SendRequest(connection, CancellationToken.None, Frame(6, 1, 0, new byte[8]));
            Assert.That(source.ToArray().Length, Is.EqualTo(10 + 17), "The canceled queued frame must not be written.");
        }

        [Test]
        public async Task QueuedResetReturnsUnsentConnectionCreditToAWaitingSibling()
        {
            using var source = new GatedOutput();
            using var connection = (IDisposable)Connection(source, _ => Assert.Fail("A queued reset failed the connection."));
            var first = SendRequest(connection, CancellationToken.None, Frame(6, 1, 0, new byte[8]));
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var flow = ConnectionType.GetProperty("SendFlow", Flags)?.GetValue(connection)
                ?? throw new AssertionException("Missing flow control.");
            object? Call(string name, params object[] args) => (flow.GetType().GetMethod(name, Flags)
                ?? throw new AssertionException("Missing flow method.")).Invoke(flow, args);
            Call("Open", 1);
            Assert.That(await (Task<int>)(Call("ReserveAsync", 1, 49151, CancellationToken.None)
                ?? throw new AssertionException("Missing reservation.")), Is.EqualTo(49151));
            using var reset = new CancellationTokenSource();
            var queued = SendRequest(connection, reset.Token, Frame(0, 1, 3, new byte[16384]));
            Call("Open", 5);
            var sibling = (Task<int>)(Call("ReserveAsync", 5, 1, CancellationToken.None)
                ?? throw new AssertionException("Missing sibling reservation."));
            Assert.That(sibling.IsCompleted, Is.False);
            try
            {
                // A reset can remove the stream before its queued writer notices.
                Call("Close", 3);
                reset.Cancel();
                await Assert.ThatAsync(async () => await queued.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>());
                Assert.That(await sibling.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1),
                    "Unsent DATA must not permanently consume the sibling's connection window.");
            }
            finally
            {
                source.Release.TrySetResult(true);
                await first.WaitAsync(TimeSpan.FromSeconds(2));
            }
            await SendRequest(connection, CancellationToken.None, Frame(6, 1, 0, new byte[8]));
            Assert.That(source.Length, Is.EqualTo(34), "Only the two complete PING frames belong on the wire.");
        }

        [Test]
        public async Task ResetAfterEncodingPreservesThePeersHpackTable()
        {
            using var source = new GatedOutput();
            var failures = 0;
            using var connection = (IDisposable)Connection(source, _ => Interlocked.Increment(ref failures));
            using var reset = new CancellationTokenSource();
            var fieldType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true)
                ?? throw new AssertionException("Missing HPACK field.");
            var fields = Array.CreateInstance(fieldType, 1);
            fields.SetValue(Activator.CreateInstance(fieldType, Flags, null,
                new object[] { "x-boundary", "shared-value", false }, null), 0);
            var headers = ConnectionType.GetMethod("SendHeadersAsync", Flags) ?? throw new AssertionException("Missing header writer.");
            Task Send(int id, CancellationToken token) => (Task)(headers.Invoke(connection, new object[] { id, fields, true, token })
                ?? throw new AssertionException("Missing header task."));
            var first = Send(1, reset.Token);
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            reset.Cancel();
            source.Release.TrySetResult(true);
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await Send(3, CancellationToken.None);
            using var wire = new MemoryStream(source.ToArray());
            using var reader = (IDisposable)Transport(wire);
            var decoderType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackDecoder", true)
                ?? throw new AssertionException("Missing HPACK decoder.");
            var decoder = Activator.CreateInstance(decoderType, Flags, null, new object[] { 32768 }, null)
                ?? throw new AssertionException("Missing HPACK decoder constructor.");
            var decode = decoderType.GetMethod("Decode", Flags) ?? throw new AssertionException("Missing HPACK decode method.");
            foreach (var id in new[] { 1, 3 })
            {
                var frame = await Read(reader) ?? throw new AssertionException("Missing complete header frame.");
                Assert.That(Property<int>(frame, "StreamId"), Is.EqualTo(id));
                var decoded = (Array)(decode.Invoke(decoder, new object[] { Property<byte[]>(frame, "Payload") })
                    ?? throw new AssertionException("Missing decoded fields."));
                Assert.That(decoded.Length, Is.EqualTo(1));
                var field = decoded.GetValue(0) ?? throw new AssertionException("Missing decoded field.");
                Assert.That(fieldType.GetProperty("Name")?.GetValue(field), Is.EqualTo("x-boundary"));
                Assert.That(fieldType.GetProperty("Value")?.GetValue(field), Is.EqualTo("shared-value"));
            }
            Assert.That(await Read(reader), Is.Null);
            Assert.That(failures, Is.Zero);
        }

        [Test]
        public async Task OwningConnectionCancellationStillInterruptsSharedOutput()
        {
            using var source = new GatedOutput();
            var failures = 0;
            using var connection = (IDisposable)Connection(source, _ => Interlocked.Increment(ref failures));
            using var stop = new CancellationTokenSource();
            (ConnectionType.GetMethod("UseTransportCancellation", Flags) ?? throw new AssertionException("Missing connection cancellation hook."))
                .Invoke(connection, new object[] { stop.Token });
            var first = SendRequest(connection, CancellationToken.None, Frame(0, 1, 1, new byte[] { 10 }));
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stop.Cancel();
            await Assert.ThatAsync(async () => await first.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(failures, Is.EqualTo(1));
            await Assert.ThatAsync(async () => await SendRequest(connection, CancellationToken.None, Frame(6, 1, 0, new byte[8])),
                Throws.InstanceOf<IOException>());
        }

        private sealed class GatedOutput : MemoryStream
        {
            private int _writes;
            internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Interlocked.Increment(ref _writes) == 1)
                {
                    // A partial write makes cancellation unsafe for sibling streams.
                    base.Write(buffer, offset, 4);
                    Started.TrySetResult(true);
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                    base.Write(buffer, offset + 4, count - 4);
                }
                else base.Write(buffer, offset, count);
            }
        }
    }
}
