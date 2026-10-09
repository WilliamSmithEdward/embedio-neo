using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class DeflateRequestStreamTest
    {
        private static Stream Decoder(Stream source, bool zlib)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.DeflateRequestStream")
                ?? throw new AssertionException("Missing strict DEFLATE stream.");
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { source, zlib }, null) as Stream ?? throw new AssertionException("Missing stream instance.");
        }
        private static byte[] Encode(byte[] body, bool zlib)
        {
            if (body.Length == 0) return zlib ? new byte[] { 0x78, 0x9c, 3, 0, 0, 0, 0, 1 } : new byte[] { 3, 0 };
            using var output = new MemoryStream();
            using (Stream encoder = zlib ? new ZLibStream(output, CompressionLevel.SmallestSize, true)
                : new DeflateStream(output, CompressionLevel.SmallestSize, true)) encoder.Write(body);
            return output.ToArray();
        }
        private sealed class FragmentedSource(byte[] data, int chunk) : MemoryStream(data, false)
        {
            internal int Reads;
            internal int Disposals;
            public override int Read(byte[] buffer, int offset, int count)
            {
                Reads++;
                return base.Read(buffer, offset, Math.Min(count, chunk));
            }
            public override int Read(Span<byte> buffer)
            {
                Reads++;
                return base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(Read(buffer.Span));
            }
            protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
        }
        private sealed class DelayedSource(byte[] bytes) : MemoryStream(bytes, false)
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Disposals;
            private bool _closed;
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                ObjectDisposedException.ThrowIf(_closed, this);
                return Read(buffer, offset, count);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Disposals++; _closed = true; Release.TrySetResult(); }
                base.Dispose(disposing);
            }
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task DisposalWakesPendingReadAndReleasesSourceOnce(bool zlib)
        {
            using var source = new DelayedSource(Encode(new byte[] { 1, 2, 3 }, zlib));
            using var decoder = Decoder(source, zlib);
            var pending = decoder.ReadAsync(new byte[1], 0, 1);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<InvalidOperationException>(() => Assert.That(decoder.ReadByte(), Is.EqualTo(1)));
            decoder.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(10)), Is.Zero));
            decoder.Dispose();
            Assert.That(source.Disposals, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => Assert.That(decoder.ReadByte(), Is.EqualTo(-1)));
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationDuringSourceReadIsStickyAndPreservesToken(bool zlib)
        {
            using var source = new DelayedSource(Encode(new byte[] { 1, 2, 3 }, zlib));
            using var decoder = Decoder(source, zlib);
            using var cancelled = new CancellationTokenSource();
            var pending = decoder.ReadAsync(new byte[1], 0, 1, cancelled.Token);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancelled.Cancel();
            var error = await Assert.CatchAsync<OperationCanceledException>(async () => Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(10)), Is.Zero));
            Assert.That(error.CancellationToken, Is.EqualTo(cancelled.Token));
            Assert.Throws<HttpException>(() => Assert.That(decoder.ReadByte(), Is.EqualTo(-1)));
        }
        public static IEnumerable ValidCases()
        {
            foreach (var zlib in new[] { false, true })
                foreach (var length in new[] { 0, 257, 65536 })
                    foreach (var chunk in new[] { 1, 113 })
                        foreach (var read in new[] { "array", "span", "memory", "async-array", "byte" })
                            yield return new object[] { zlib, length, chunk, read };
        }
        [TestCaseSource(nameof(ValidCases))]
        public async Task RoundTripValidatesCompletionAcrossEveryReadSurface(bool zlib, int length, int chunk, string read)
        {
            var body = new byte[length];
            new Random(20261009).NextBytes(body);
            using var source = new FragmentedSource(Encode(body, zlib), chunk);
            using var decoder = Decoder(source, zlib);
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                int count;
                if (read == "byte")
                {
                    var value = decoder.ReadByte();
                    if (value < 0) break;
                    result.WriteByte((byte)value);
                    continue;
                }
                if (read == "span") count = decoder.Read(buffer.AsSpan());
                else if (read == "memory") count = await decoder.ReadAsync(buffer.AsMemory());
                else if (read == "async-array") count = await decoder.ReadAsync(buffer, 0, buffer.Length);
                else count = decoder.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                result.Write(buffer, 0, count);
            }
            Assert.That(result.ToArray(), Is.EqualTo(body));
            Assert.That(decoder.Read(buffer, 0, buffer.Length), Is.Zero);
            decoder.Dispose();
            decoder.Dispose();
            Assert.That(source.Disposals, Is.EqualTo(1));
        }
        private static byte[] WindowBody(int cmf)
        {
            var raw = new byte[309];
            raw[1] = 0x2c; raw[2] = 1; raw[3] = 0xd3; raw[4] = 0xfe;
            Array.Fill(raw, (byte)0x41, 5, 300);
            // Final fixed block: length 3, distance 300, end-of-block.
            var bits = 3 | (64 << 3) | (1 << 10) | (43 << 15);
            for (var index = 0; index < 4; index++) raw[305 + index] = (byte)(bits >> (index * 8));
            var result = new byte[raw.Length + 6];
            Header(cmf, 0).CopyTo(result, 0);
            raw.CopyTo(result, 2);
            uint low = 1;
            uint high = 0;
            for (var index = 0; index < 303; index++) { low = (low + 65) % 65521; high = (high + low) % 65521; }
            var checksum = (high << 16) | low;
            for (var index = 0; index < 4; index++) result[result.Length - 4 + index] = (byte)(checksum >> (24 - index * 8));
            return result;
        }
        [Test]
        public void AdvertisedWindowAllowsAnInRangeDistance()
        {
            using var source = new FragmentedSource(WindowBody(0x18), 1);
            using var decoder = Decoder(source, true);
            using var output = new MemoryStream();
            decoder.CopyTo(output);
            Assert.That(output.ToArray(), Is.EqualTo(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat((byte)0x41, 303))));
        }
        public static IEnumerable InvalidCases()
        {
            var body = System.Text.Encoding.ASCII.GetBytes("complete Adler-32 framing validation");
            var zlib = Encode(body, true);
            var raw = Encode(body, false);
            var badChecksum = (byte[])zlib.Clone();
            badChecksum[^1] ^= 1;
            var trailing = new byte[zlib.Length + 1];
            zlib.CopyTo(trailing, 0);
            trailing[^1] = 0x42;
            var spoofedTrailer = new byte[zlib.Length + 4];
            zlib.CopyTo(spoofedTrailer, 0);
            Array.Copy(zlib, zlib.Length - 4, spoofedTrailer, zlib.Length, 4);
            var rawTrailing = new byte[raw.Length + 1];
            raw.CopyTo(rawTrailing, 0);
            var cases = new (string Name, byte[] Bytes, bool Zlib)[]
            {
                ("missing-header", Array.Empty<byte>(), true),
                ("partial-header", new byte[] { 0x78 }, true),
                ("bad-header-check", new byte[] { 0x78, 0 }, true),
                ("bad-method", Header(0x77, 0), true),
                ("reserved-window", Header(0x88, 0), true),
                ("preset-dictionary", Header(0x78, 32), true),
                ("distance-beyond-advertised-window", WindowBody(0x08), true),
                ("missing-checksum", zlib[..^4], true),
                ("partial-checksum", zlib[..^1], true),
                ("bad-checksum", badChecksum, true),
                ("trailing-byte", trailing, true),
                ("spoofed-extra-checksum", spoofedTrailer, true),
                ("raw-empty", Array.Empty<byte>(), false),
                ("raw-truncated", raw[..^1], false),
                ("raw-trailing", rawTrailing, false),
                ("raw-reserved-block", new byte[] { 7 }, false)
            };
            foreach (var item in cases)
                foreach (var chunk in new[] { 1, 8192 })
                    foreach (var asynchronous in new[] { false, true })
                        yield return new object[] { item.Name, item.Bytes, item.Zlib, chunk, asynchronous };
        }
        private static byte[] Header(int cmf, int flags)
        {
            var flg = flags + ((31 - (((cmf << 8) | flags) % 31)) % 31);
            return new[] { (byte)cmf, (byte)flg };
        }
        [TestCaseSource(nameof(InvalidCases))]
        public async Task MalformedEnvelopeBecomesStickyBadRequest(string name, byte[] bytes, bool zlib, int chunk, bool asynchronous)
        {
            Assert.That(name, Is.Not.Empty);
            using var source = new FragmentedSource(bytes, chunk);
            using var decoder = Decoder(source, zlib);
            var buffer = new byte[257];
            if (asynchronous)
            {
                var error = await Assert.ThrowsAsync<HttpException>(async () =>
                {
                    while (await decoder.ReadAsync(buffer.AsMemory()) != 0) { }
                });
                Assert.That(error.StatusCode, Is.EqualTo(400));
            }
            else
            {
                var error = Assert.Throws<HttpException>(() => { while (decoder.Read(buffer, 0, buffer.Length) != 0) { } });
                Assert.That(error.StatusCode, Is.EqualTo(400));
            }
            Assert.Throws<HttpException>(() => Assert.That(decoder.Read(buffer, 0, buffer.Length), Is.Zero));
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task PrecancelledReadDoesNotTouchOrPoisonTheSource(bool zlib)
        {
            using var source = new FragmentedSource(Encode(new byte[] { 1, 2, 3 }, zlib), 1);
            using var decoder = Decoder(source, zlib);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var error = await Assert.CatchAsync<OperationCanceledException>(async () => Assert.That(await decoder.ReadAsync(new byte[1], cancelled.Token), Is.Zero));
            Assert.That(error.CancellationToken, Is.EqualTo(cancelled.Token));
            Assert.That(source.Reads, Is.Zero);
            Assert.That(decoder.ReadByte(), Is.EqualTo(1));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ZeroLengthReadDoesNotConsumeHeaderOrVerifyCompletion(bool zlib)
        {
            using var source = new FragmentedSource(Encode(new byte[] { 1 }, zlib), 1);
            using var decoder = Decoder(source, zlib);
            Assert.That(decoder.Read(Array.Empty<byte>(), 0, 0), Is.Zero);
            Assert.That(source.Reads, Is.Zero);
            Assert.That(decoder.ReadByte(), Is.EqualTo(1));
            Assert.That(decoder.ReadByte(), Is.EqualTo(-1));
        }
    }
}
