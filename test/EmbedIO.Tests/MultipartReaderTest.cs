using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class MultipartReaderTest
    {
        private const string Boundary = "upload-123";
        private const string FieldHeader = "Content-Disposition: form-data; name=\"field\"\r\n";
        private const string FileHeader = "Content-Disposition: form-data; name=\"file\"; filename=\"test.bin\"\r\n";

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(7)]
        [TestCase(8192)]
        public async Task BinaryFilesAndRepeatedFieldsSurviveFragmentation(int fragment)
        {
            var payload = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            var wire = Join(Encoding.UTF8.GetBytes("preamble\r\n--" + Boundary + "\r\n" + FileHeader + "Content-Type: application/octet-stream\r\n\r\n"),
                payload, Encoding.UTF8.GetBytes("\r\n--" + Boundary + "\r\n" + FieldHeader + "\r\nfirst\r\n--" + Boundary + "\r\n" + FieldHeader + "\r\n\r\n--" + Boundary + "--\r\nepilogue"));
            using var input = new FragmentedStream(wire, fragment);
            using var reader = new MultipartReader(input, Boundary);
            var file = await Next(reader);
            Assert.That(file.Name, Is.EqualTo("file"));
            Assert.That(file.FileName, Is.EqualTo("test.bin"));
            Assert.That(file.ContentType, Is.EqualTo("application/octet-stream"));
            Assert.That(await Bytes(file.Body), Is.EqualTo(payload));
            var first = await Next(reader);
            Assert.That(first.Name, Is.EqualTo("field"));
            Assert.That(first.FileName, Is.Null);
            Assert.That(await Bytes(first.Body), Is.EqualTo(Encoding.UTF8.GetBytes("first")));
            var empty = await Next(reader);
            Assert.That(empty.Name, Is.EqualTo("field"));
            Assert.That(await Bytes(empty.Body), Is.Empty);
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(11)]
        public async Task BoundaryLikeBytesArePreserved(int fragment)
        {
            var body = "start\r\n--upload-123X\r\n--upload-123-?\r\n--upload-12\r\n--upload-123\rend";
            using var input = new FragmentedStream(Wire(FieldHeader, body), fragment);
            using var reader = new MultipartReader(input, Boundary);
            var section = await Next(reader);
            Assert.That(await Bytes(section.Body), Is.EqualTo(Encoding.UTF8.GetBytes(body)));
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [TestCase("")]
        [TestCase("\r\n")]
        public async Task ClosingDelimiterAcceptsOptionalFinalCrLf(string ending)
        {
            using var input = new MemoryStream(Wire(FieldHeader, "value", ending));
            using var reader = new MultipartReader(input, Boundary);
            Assert.That(await Bytes((await Next(reader)).Body), Is.EqualTo(Encoding.UTF8.GetBytes("value")));
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [Test]
        public async Task AdvancingDrainsUnreadBodyAndInvalidatesOldStream()
        {
            using var input = new MemoryStream(TwoFields());
            using var reader = new MultipartReader(input, Boundary);
            var first = await Next(reader);
            var second = await Next(reader);
            await Assert.ThatAsync(() => first.Body.ReadAsync(new byte[1], 0, 1), Throws.InstanceOf<InvalidOperationException>().Or.InstanceOf<ObjectDisposedException>());
            first.Body.Dispose();
            Assert.That(await Bytes(second.Body), Is.EqualTo(Encoding.UTF8.GetBytes("second")));
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [Test]
        public async Task DisposingBodyStillAllowsAdvancing()
        {
            using var input = new MemoryStream(TwoFields());
            using var reader = new MultipartReader(input, Boundary);
            var first = await Next(reader);
            first.Body.Dispose();
            Assert.That(await Bytes((await Next(reader)).Body), Is.EqualTo(Encoding.UTF8.GetBytes("second")));
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [Test]
        public async Task ReaderBorrowsInputAndInvalidatesCurrentBodyOnDisposal()
        {
            using var input = new MemoryStream(Wire(FieldHeader, "value"));
            var reader = new MultipartReader(input, Boundary);
            var section = await Next(reader);
            reader.Dispose();
            reader.Dispose();
            Assert.That(input.CanRead, Is.True);
            await Assert.ThatAsync(() => section.Body.ReadAsync(new byte[1], 0, 1), Throws.InstanceOf<ObjectDisposedException>());
            await Assert.ThatAsync(() => reader.ReadNextSectionAsync(), Throws.InstanceOf<ObjectDisposedException>());
        }

        [Test]
        public async Task OpeningFileDoesNotReadWholePayload()
        {
            using var input = new FragmentedStream(Wire(FileHeader, new string('x', 2 * 1024 * 1024)), int.MaxValue);
            using var reader = new MultipartReader(input, Boundary);
            Assert.That(input.BytesRead, Is.Zero);
            var section = await Next(reader);
            Assert.That(input.BytesRead, Is.LessThan(32768));
            Assert.That(await section.Body.ReadAsync(new byte[17], 0, 17), Is.EqualTo(17));
            Assert.That(input.BytesRead, Is.LessThan(32768));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task CancellationIsObservedForBufferedBody(bool lifetime)
        {
            using var cancellation = new CancellationTokenSource();
            using var input = new MemoryStream(Wire(FileHeader, "payload"));
            using var reader = new MultipartReader(input, Boundary, cancellationToken: lifetime ? cancellation.Token : default);
            var section = await Next(reader);
            cancellation.Cancel();
            await Assert.ThatAsync(() => section.Body.ReadAsync(new byte[1], 0, 1, lifetime ? default : cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        }

        [TestCase("lifetime")]
        [TestCase("operation")]
        [TestCase("dispose")]
        public async Task PendingInputReadCanBeInterrupted(string cancellationKind)
        {
            using var cancellation = new CancellationTokenSource();
            using var input = new BlockingStream();
            using var reader = new MultipartReader(input, Boundary, cancellationToken: cancellationKind == "lifetime" ? cancellation.Token : default);
            var pending = reader.ReadNextSectionAsync(cancellationKind == "operation" ? cancellation.Token : default);
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancellationKind == "dispose") reader.Dispose();
            else cancellation.Cancel();
            await Assert.ThatAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(input.CanRead, Is.True);
        }

        [Test]
        public async Task ZeroLengthReadDoesNotConsumeBody()
        {
            using var input = new FragmentedStream(Wire(FieldHeader, "body"), 1);
            using var reader = new MultipartReader(input, Boundary);
            var section = await Next(reader);
            var before = input.BytesRead;
            Assert.That(await section.Body.ReadAsync(Array.Empty<byte>(), 0, 0), Is.Zero);
            Assert.That(input.BytesRead, Is.EqualTo(before));
            Assert.That(await Bytes(section.Body), Is.EqualTo(Encoding.UTF8.GetBytes("body")));
        }

        [Test]
        public async Task EmptyMultipartAcceptsZeroPartLimits()
        {
            using var input = new MemoryStream(Encoding.ASCII.GetBytes("--" + Boundary + "--\r\n"));
            using var reader = new MultipartReader(input, Boundary, new MultipartOptions { MaxParts = 0, MaxFiles = 0, MaxFields = 0 });
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [Test]
        public async Task ConcurrentOperationIsRejectedWithoutInterruptingPendingRead()
        {
            using var cancellation = new CancellationTokenSource();
            using var input = new BlockingStream();
            using var reader = new MultipartReader(input, Boundary, cancellationToken: cancellation.Token);
            var pending = reader.ReadNextSectionAsync();
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThatAsync(() => reader.ReadNextSectionAsync(), Throws.InstanceOf<InvalidOperationException>());
            Assert.That(pending.IsCompleted, Is.False);
            cancellation.Cancel();
            await Assert.ThatAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public async Task OpaquePreambleDoesNotRequireUtf8()
        {
            var wire = Join(new byte[] { 0xFF, 0xC0, 0xAF, 0, 13, 10 }, Wire(FieldHeader, "body"));
            using var input = new FragmentedStream(wire, 1);
            using var reader = new MultipartReader(input, Boundary);
            Assert.That(await Bytes((await Next(reader)).Body), Is.EqualTo(Encoding.UTF8.GetBytes("body")));
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [TestCase(1)]
        [TestCase(5)]
        public async Task DelimitersAcceptTransportPadding(int fragment)
        {
            var wire = Encoding.ASCII.GetBytes("--" + Boundary + " \t\r\n" + FieldHeader + "\r\nfirst\r\n--" + Boundary
                + "\t \r\n" + FieldHeader + "\r\nsecond\r\n--" + Boundary + "-- \t\r\n");
            using var input = new FragmentedStream(wire, fragment);
            using var reader = new MultipartReader(input, Boundary);
            Assert.That(await Bytes((await Next(reader)).Body), Is.EqualTo(Encoding.ASCII.GetBytes("first")));
            Assert.That(await Bytes((await Next(reader)).Body), Is.EqualTo(Encoding.ASCII.GetBytes("second")));
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        [TestCase(1)]
        [TestCase(69)]
        [TestCase(8192)]
        public async Task MaximumBoundarySurvivesFragmentation(int fragment)
        {
            var boundary = new string('a', 70);
            var payload = new string('x', 16384);
            var wire = Encoding.ASCII.GetBytes("--" + boundary + "\r\n" + FileHeader + "\r\n" + payload + "\r\n--" + boundary + "--");
            using var input = new FragmentedStream(wire, fragment);
            using var reader = new MultipartReader(input, boundary);
            Assert.That(await Bytes((await Next(reader)).Body), Is.EqualTo(Encoding.ASCII.GetBytes(payload)));
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
        }

        private static byte[] Wire(string headers, string body, string ending = "\r\n")
            => Encoding.UTF8.GetBytes("--" + Boundary + "\r\n" + headers + "\r\n" + body + "\r\n--" + Boundary + "--" + ending);

        private static byte[] TwoFields()
            => Wire(FieldHeader, "first\r\n--" + Boundary + "\r\n" + FieldHeader + "\r\nsecond");

        private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(bytes => bytes).ToArray();

        private static async Task<MultipartSection> Next(MultipartReader reader)
            => await reader.ReadNextSectionAsync() ?? throw new AssertionException("Expected another multipart section.");

        private static async Task<byte[]> Bytes(Stream stream)
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            return output.ToArray();
        }

        private sealed class BlockingStream : MemoryStream
        {
            public TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }

        private sealed class FragmentedStream : MemoryStream
        {
            private readonly int _fragment;
            public FragmentedStream(byte[] bytes, int fragment) : base(bytes) => _fragment = fragment;
            public long BytesRead { get; private set; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = base.Read(buffer, offset, Math.Min(count, _fragment));
                BytesRead += read;
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = base.Read(buffer.Span.Slice(0, Math.Min(buffer.Length, _fragment)));
                BytesRead += read;
                return new ValueTask<int>(read);
            }
        }
    }
}
