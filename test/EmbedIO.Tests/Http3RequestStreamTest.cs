using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3WireTest
    {
        private sealed class RequestReader
        {
            private readonly object _instance;
            internal RequestReader(Stream source, long maximumBody = long.MaxValue, int metadata = 65536)
                => _instance = Activator.CreateInstance(InternalType("Http3RequestStream"), Hidden, null,
                    new object[] { 1099511627776L, source, metadata, maximumBody }, null) ?? throw new AssertionException("Missing request reader.");
            private object? Call(string method, params object?[] args)
            {
                try { return (_instance.GetType().GetMethod(method, Hidden) ?? throw new AssertionException("Missing request operation.")).Invoke(_instance, args); }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
            }
            internal async Task<object> Next(CancellationToken token = default)
            {
                var task = AsTask(Call("ReadEventAsync", token));
                await task;
                return task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new AssertionException("Missing event.");
            }
            internal void Headers(long? length = null) => Call("ConfirmHeaders", new object?[] { length });
            internal void Trailers() => Call("ConfirmTrailers");
            internal void Tunnel() => Call("EnterTunnel");
            internal Task<int> Data(byte[] bytes, CancellationToken token = default) =>
                (Task<int>)AsTask(Call("ReadDataAsync", bytes, 0, bytes.Length, token));
            internal long Bytes => Property(_instance, "BodyBytes");
            internal (object? Event, Task? Pending) TryWithoutWaiting()
            {
                var args = new object?[] { null };
                var next = Call("TryReadEventWithoutWaiting", args);
                return (next, (Task?)args[0]);
            }
        }
        private static string Kind(object value) => value.GetType().GetProperty("Kind")?.GetValue(value)?.ToString() ?? throw new AssertionException("Missing kind.");
        private static byte[] Frame(long type, byte[]? payload = null)
        {
            payload ??= Array.Empty<byte>();
            var bytes = new byte[16 + payload.Length];
            var offset = Encode(bytes, 0, type);
            offset += Encode(bytes, offset, payload.Length);
            Buffer.BlockCopy(payload, 0, bytes, offset, payload.Length);
            Array.Resize(ref bytes, offset + payload.Length);
            return bytes;
        }
        private static void RequestError(IOException? error, long code, bool stream)
        {
            Assert.That(Property(error, "ErrorCode"), Is.EqualTo(code));
            Assert.That(error?.GetType().Name, Is.EqualTo(stream ? "Http3StreamException" : "Http3ProtocolException"));
            if (stream) Assert.That(Property(error, "StreamId"), Is.EqualTo(1099511627776L));
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(4096)]
        public async Task RequestHeadersDataTrailersAndUnknownFramesKeepTheirOrder(int fragment)
        {
            using var source = new FragmentedStream(Convert.FromHexString("2100010200000003616263010200002100"), fragment);
            var reader = new RequestReader(source);
            var headers = await reader.Next();
            Assert.That(Kind(headers), Is.EqualTo("Headers"));
            Assert.That(headers.GetType().GetProperty("EncodedFields")?.GetValue(headers), Is.EqualTo(new byte[2]));
            await Assert.ThatAsync(async () => await reader.Next(), Throws.TypeOf<InvalidOperationException>());
            reader.Headers(3);
            Assert.That(Kind(await reader.Next()), Is.EqualTo("Data"));
            Assert.That(source.Position, Is.EqualTo(8), "DATA is not read ahead into an application queue.");
            await Assert.ThatAsync(async () => await reader.Next(), Throws.TypeOf<InvalidOperationException>());
            using var body = new MemoryStream();
            var buffer = new byte[8];
            int count;
            while ((count = await reader.Data(buffer)) != 0) body.Write(buffer, 0, count);
            Assert.That(body.ToArray(), Is.EqualTo(new byte[] { 97, 98, 99 }));
            Assert.That(reader.Bytes, Is.EqualTo(3));
            Assert.That(Kind(await reader.Next()), Is.EqualTo("Trailers"));
            await Assert.ThatAsync(async () => await reader.Next(), Throws.TypeOf<InvalidOperationException>());
            reader.Trailers();
            Assert.That(Kind(await reader.Next()), Is.EqualTo("End"));
            Assert.That(Kind(await reader.Next()), Is.EqualTo("End"));
            Assert.That(source.CanRead, Is.True);
        }

        [TestCase(2L)]
        [TestCase(3L)]
        [TestCase(4L)]
        [TestCase(5L)]
        [TestCase(6L)]
        [TestCase(7L)]
        [TestCase(8L)]
        [TestCase(9L)]
        [TestCase(13L)]
        [TestCase(0xf0700L)]
        [TestCase(0xf0701L)]
        public async Task ControlAndReservedFramesAreConnectionErrorsOnRequestStreams(long type)
        {
            using var source = new FragmentedStream(Frame(type), 1);
            var reader = new RequestReader(source);
            RequestError(await Assert.CatchAsync<IOException>(async () => await reader.Next()), 0x105, false);
        }

        [Test]
        public async Task DataBeforeHeadersIsAConnectionFramingError()
        {
            using var source = new FragmentedStream(Frame(0), 1);
            RequestError(await Assert.CatchAsync<IOException>(async () => await new RequestReader(source).Next()), 0x105, false);
        }

        [TestCase("0000")]
        [TestCase("01020000")]
        public async Task DataOrHeadersAfterTrailersAreConnectionErrors(string suffix)
        {
            using var source = new FragmentedStream(Convert.FromHexString("0102000001020000" + suffix), 1);
            var reader = new RequestReader(source);
            await reader.Next(); reader.Headers();
            await reader.Next(); reader.Trailers();
            RequestError(await Assert.CatchAsync<IOException>(async () => await reader.Next()), 0x105, false);
        }

        [TestCase("")]
        [TestCase("2100")]
        public async Task FinBeforeHeadersIsAnIncompleteRequestStream(string wire)
        {
            using var source = new FragmentedStream(Convert.FromHexString(wire), 1);
            RequestError(await Assert.CatchAsync<IOException>(async () => await new RequestReader(source).Next()), 0x10d, true);
        }

        [TestCase(1L)]
        [TestCase(3L)]
        public async Task ContentLengthMismatchKeepsTheFullWidthStreamIdentifier(long declared)
        {
            using var source = new FragmentedStream(Convert.FromHexString("0102000000026162"), 8);
            var reader = new RequestReader(source);
            await reader.Next(); reader.Headers(declared);
            if (declared == 3)
            {
                await reader.Next();
                Assert.That(await reader.Data(new byte[8]), Is.EqualTo(2));
            }
            RequestError(await Assert.CatchAsync<IOException>(async () => await reader.Next()), 0x10e, true);
        }

        [Test]
        public async Task ContentLengthIsCheckedAtTheTrailerBoundary()
        {
            using var source = new FragmentedStream(Convert.FromHexString("0102000001020000"), 8);
            var reader = new RequestReader(source);
            await reader.Next(); reader.Headers(1);
            await reader.Next();
            RequestError(Assert.Catch<IOException>(reader.Trailers), 0x10e, true);
        }

        [Test]
        public async Task HugeDataLengthIsRejectedWithoutReadingItsPayload()
        {
            using var source = new FragmentedStream(Convert.FromHexString("0102000000ffffffffffffffff"), 8);
            var reader = new RequestReader(source, 16);
            await reader.Next(); reader.Headers();
            RequestError(await Assert.CatchAsync<IOException>(async () => await reader.Next()), 0x107, true);
            Assert.That(source.Position, Is.EqualTo(source.Length));
            Assert.That(reader.Bytes, Is.Zero);
        }

        [Test]
        public async Task FinInsideDataRetainsConnectionFrameErrorScope()
        {
            using var source = new FragmentedStream(Convert.FromHexString("01020000000261"), 8);
            var reader = new RequestReader(source);
            await reader.Next(); reader.Headers(); await reader.Next();
            Assert.That(await reader.Data(new byte[8]), Is.EqualTo(1));
            RequestError(await Assert.CatchAsync<IOException>(async () => await reader.Data(new byte[8])), 0x106, false);
        }

        [Test]
        public async Task TunnelIgnoresMessageLengthAndRetainsHalfClose()
        {
            using var source = new FragmentedStream(Convert.FromHexString("0102000000026162"), 8);
            var reader = new RequestReader(source);
            await reader.Next(); reader.Headers(0); reader.Tunnel();
            await reader.Next();
            Assert.That(await reader.Data(new byte[8]), Is.EqualTo(2));
            Assert.That(Kind(await reader.Next()), Is.EqualTo("End"));
        }

        [Test]
        public async Task HeadersAfterTunnelTransitionAreConnectionErrors()
        {
            using var source = new FragmentedStream(Convert.FromHexString("0102000001020000"), 8);
            var reader = new RequestReader(source);
            await reader.Next(); reader.Headers(); reader.Tunnel();
            RequestError(await Assert.CatchAsync<IOException>(async () => await reader.Next()), 0x105, false);
        }

        [Test]
        public async Task RequestReaderRejectsConcurrentReadsAndPoisonsCanceledInput()
        {
            using var source = new PendingStream();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reader = new RequestReader(source);
            var pending = reader.Next(stop.Token);
            await source.Entered.Task.WaitAsync(stop.Token);
            await Assert.ThatAsync(async () => await reader.Next(), Throws.TypeOf<InvalidOperationException>());
            stop.Cancel();
            await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            await Assert.ThatAsync(async () => await reader.Next(), Throws.TypeOf<IOException>());
        }

        [Test]
        public async Task EmptyDataFrameDoesNotEndTheRequest()
        {
            using var source = new FragmentedStream(Convert.FromHexString("010200000000000161"), 8);
            var reader = new RequestReader(source);
            await reader.Next(); reader.Headers(1);
            Assert.That(Kind(await reader.Next()), Is.EqualTo("Data"));
            Assert.That(await reader.Data(new byte[8]), Is.Zero);
            Assert.That(Kind(await reader.Next()), Is.EqualTo("Data"));
            Assert.That(await reader.Data(new byte[8]), Is.EqualTo(1));
            Assert.That(Kind(await reader.Next()), Is.EqualTo("End"));
        }

        [Test]
        public async Task RequestMetadataLimitRejectsBeforeBuffering()
        {
            using var source = new FragmentedStream(Convert.FromHexString("01ffffffffffffffff"), 8);
            var reader = new RequestReader(source, metadata: 8);
            RequestError(await Assert.CatchAsync<IOException>(async () => await reader.Next()), 0x107, true);
            Assert.That(source.Position, Is.EqualTo(9));
        }

        [Test]
        public async Task BodyBudgetCountsDataAcrossFrames()
        {
            using var source = new FragmentedStream(Convert.FromHexString("01020000000361626300026465"), 8);
            var reader = new RequestReader(source, 4);
            await reader.Next(); reader.Headers(); await reader.Next();
            Assert.That(await reader.Data(new byte[8]), Is.EqualTo(3));
            RequestError(await Assert.CatchAsync<IOException>(async () => await reader.Next()), 0x107, true);
            Assert.That(reader.Bytes, Is.EqualTo(3));
            Assert.That(source.Position, Is.EqualTo(11));
        }

        [Test]
        public async Task DeclaredBodyBudgetRejectsBeforeAnyData()
        {
            using var source = new FragmentedStream(Convert.FromHexString("01020000"), 8);
            var reader = new RequestReader(source, 4);
            await reader.Next();
            RequestError(Assert.Catch<IOException>(() => reader.Headers(5)), 0x107, true);
        }

        private sealed class HeadersThenPendingFin : MemoryStream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ArrayRead(buffer, cancellationToken, ReadAsync);
            internal HeadersThenPendingFin() : base(new byte[] { 1, 2, 0, 0 }) { }
            internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Position < Length) return base.Read(buffer, offset, count);
                Entered.TrySetResult(true);
                await Resume.Task.WaitAsync(cancellationToken);
                return 0;
            }
        }

        [Test]
        public async Task ConnectCanCommitWhileInputWaitsForFin()
        {
            using var source = new HeadersThenPendingFin();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reader = new RequestReader(source);
            await reader.Next(stop.Token); reader.Headers(1);
            var pending = reader.Next(stop.Token);
            await source.Entered.Task.WaitAsync(stop.Token);
            reader.Tunnel();
            source.Resume.TrySetResult(true);
            Assert.That(Kind(await pending), Is.EqualTo("End"));
        }

        // Returns the probe's event (null when it could not complete at once) and
        // the read it left pending, if any.
        private static (object? Event, Task? Pending) Probe(RequestReader reader) => reader.TryWithoutWaiting();

        [TestCase("01020000", "End")]
        [TestCase("010200000003616263", "Data")]
        [TestCase("010200002100", "End")]
        public async Task InputEndProbeUsesOnlyReceivedBytes(string wire, string expected)
        {
            // HEADERS, then FIN; DATA; or an unknown reserved frame (0x21) before FIN.
            using var source = new FragmentedStream(Convert.FromHexString(wire), 4096);
            var reader = new RequestReader(source);
            Assert.That(Kind(await reader.Next()), Is.EqualTo("Headers"));
            reader.Headers();
            var (next, pending) = Probe(reader);
            Assert.That(pending, Is.Null);
            Assert.That(Kind(next ?? throw new AssertionException("Missing event.")), Is.EqualTo(expected));
        }

        [Test]
        public async Task InputEndProbeNeverWaitsForThePeer()
        {
            using var source = new HeadersThenPendingFin();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reader = new RequestReader(source);
            await reader.Next(stop.Token); reader.Headers(0);
            var (next, pending) = Probe(reader);
            Assert.That(next, Is.Null, "A FIN that has not arrived is not waited for.");
            var read = pending ?? throw new AssertionException("The unfinished read must be returned.");
            Assert.That(read.IsCompleted, Is.False);
            source.Resume.TrySetResult(true);
            await read.WaitAsync(stop.Token);
        }

        [Test]
        public async Task RequestPrecancellationDoesNotConsumeInput()
        {
            using var source = new FragmentedStream(Convert.FromHexString("01020000"), 8);
            var reader = new RequestReader(source);
            await Assert.ThatAsync(async () => await reader.Next(new CancellationToken(true)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(source.Position, Is.Zero);
            Assert.That(Kind(await reader.Next()), Is.EqualTo("Headers"));
        }
    }
}
