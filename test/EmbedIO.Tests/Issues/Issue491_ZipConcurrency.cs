using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue491_ZipConcurrency
    {
        [TestCase(CompressionLevel.NoCompression, false)]
        [TestCase(CompressionLevel.NoCompression, true)]
        [TestCase(CompressionLevel.Optimal, false)]
        [TestCase(CompressionLevel.Optimal, true)]
        public async Task OpenReadersPreserveTheirOwnCursorDuringAnotherRead(CompressionLevel compression, bool asynchronous)
        {
            using var raw = new PausedArchive(CreateArchive(compression));
            using var provider = new ZipFileProvider(raw, leaveOpen: true);
            using var first = provider.OpenFile("a.bin");
            using var second = provider.OpenFile("b.bin");
            raw.Arm();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var a = Task.Run(() => ReadByte(first, asynchronous));
            Task<int>? b = null;
            try
            {
                await raw.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
                b = Task.Run(async () => { started.SetResult(true); return await ReadByte(second, asynchronous); });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Allow an unguarded competing read to finish, while a guarded read waits.
                await Task.WhenAny(b, Task.Delay(250));
            }
            finally { raw.Release(); }
            Assert.That(await a.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo((int)'a'));
            Assert.That(await b!.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo((int)'b'));
        }

        [TestCase(CompressionLevel.NoCompression)]
        [TestCase(CompressionLevel.Optimal)]
        public async Task CancelledQueuedReadDoesNotConsumeDataOrStopAnotherReader(CompressionLevel compression)
        {
            using var raw = new PausedArchive(CreateArchive(compression));
            using var provider = new ZipFileProvider(raw, true);
            using var first = provider.OpenFile("a.bin");
            using var second = provider.OpenFile("b.bin");
            raw.Arm();
            var a = Task.Run(() => ReadByte(first, true));
            try
            {
                await raw.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancel = new CancellationTokenSource();
                var queued = second.ReadAsync(new byte[1], 0, 1, cancel.Token);
                cancel.Cancel();
                await Assert.ThatAsync(async () => await queued.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
            }
            finally { raw.Release(); }
            Assert.That(await a.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo((int)'a'));
            Assert.That(await ReadByte(second, true), Is.EqualTo((int)'b'));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ProviderDisposalDoesNotWaitForAnActiveReadAndPreservesOwnership(bool leaveOpen)
        {
            using var raw = new PausedArchive(CreateArchive(CompressionLevel.NoCompression));
            var provider = new ZipFileProvider(raw, leaveOpen);
            using var first = provider.OpenFile("a.bin");
            raw.Arm();
            var a = Task.Run(() => ReadByte(first, false));
            try
            {
                await raw.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(provider.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
                provider.Dispose();
                Assert.That(raw.CanRead, Is.EqualTo(leaveOpen));
                Assert.Throws<ObjectDisposedException>(() => provider.OpenFile("a.bin"));
            }
            finally { raw.Release(); }
            if (leaveOpen)
            {
                Assert.That(await a.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo((int)'a'));
                Assert.That(first.ReadByte(), Is.EqualTo((int)'a'));
            }
            else
            {
                try { await a.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
            }
        }

        [TestCase(CompressionLevel.NoCompression, false)]
        [TestCase(CompressionLevel.NoCompression, true)]
        [TestCase(CompressionLevel.Optimal, false)]
        [TestCase(CompressionLevel.Optimal, true)]
        public async Task ParallelHttpReturnsExactEntryBytesWithAndWithoutCaching(CompressionLevel compression, bool cache)
        {
            var url = TestObjects.Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new FileModule("/assets", new ZipFileProvider(CreateArchive(compression)))
                { Cache = new FileCache { MaxFileSizeKb = cache ? 200 : 0 } });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                await Task.WhenAll(System.Linq.Enumerable.Range(0, 32).Select(async index =>
                {
                    var letter = index % 2 == 0 ? 'a' : 'b';
                    using var response = await client.GetAsync(url + "assets/" + letter + ".bin");
                    Assert.That((int)response.StatusCode, Is.EqualTo(200));
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    Assert.That(bytes.Length, Is.EqualTo(131072));
                    Assert.That(bytes, Is.All.EqualTo((byte)letter));
                }));
                using var missing = await client.GetAsync(url + "assets/missing.bin");
                Assert.That((int)missing.StatusCode, Is.EqualTo(404));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(CompressionLevel.NoCompression, false)]
        [TestCase(CompressionLevel.NoCompression, true)]
        [TestCase(CompressionLevel.Optimal, false)]
        [TestCase(CompressionLevel.Optimal, true)]
        public async Task StreamCapabilitiesAndErrorsMatchTheUnderlyingEntry(CompressionLevel compression, bool closed)
        {
            using var archiveBytes = CreateArchive(compression);
            using var baseline = new ZipArchive(archiveBytes, ZipArchiveMode.Read, true);
            using var expected = baseline.GetEntry("a.bin")!.Open();
            using var actualArchive = CreateArchive(compression);
            using var provider = new ZipFileProvider(actualArchive, true);
            using var actual = provider.OpenFile("a.bin");
            if (closed) { expected.Dispose(); actual.Dispose(); baseline.Dispose(); provider.Dispose(); }
            Assert.That((actual.CanRead, actual.CanSeek, actual.CanWrite, actual.CanTimeout),
                Is.EqualTo((expected.CanRead, expected.CanSeek, expected.CanWrite, expected.CanTimeout)));
            var operations = new Func<Stream, Task>[]
            {
                stream => { _ = stream.Length; return Task.CompletedTask; },
                stream => { _ = stream.Position; return Task.CompletedTask; },
                stream => { stream.Position = 0; return Task.CompletedTask; },
                stream => { stream.Seek(0, SeekOrigin.Begin); return Task.CompletedTask; },
                stream => { stream.SetLength(0); return Task.CompletedTask; },
                stream => { stream.Flush(); return Task.CompletedTask; },
                stream => stream.FlushAsync(CancellationToken.None),
                stream => { stream.Write(new byte[1], 0, 1); return Task.CompletedTask; },
                stream => stream.WriteAsync(new byte[1], 0, 1),
                stream => { stream.Read(null!, 0, 1); return Task.CompletedTask; },
                stream => { stream.Read(new byte[1], -1, 1); return Task.CompletedTask; },
                stream => { stream.Read(new byte[1], 0, -1); return Task.CompletedTask; },
                stream => { stream.Read(new byte[1], 1, 1); return Task.CompletedTask; },
                stream => stream.ReadAsync(null!, 0, 1),
                stream => stream.ReadAsync(new byte[1], -1, 1),
                stream => { _ = stream.ReadTimeout; return Task.CompletedTask; },
                stream => { _ = stream.WriteTimeout; return Task.CompletedTask; },
            };
            for (var index = 0; index < operations.Length; index++)
                Assert.That(await Outcome(actual, operations[index]), Is.EqualTo(await Outcome(expected, operations[index])), "operation " + index);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NullEntryNameKeepsItsOriginalArgumentError(bool closed)
        {
            using var bytes = CreateArchive(CompressionLevel.NoCompression);
            using var zip = new ZipArchive(bytes, ZipArchiveMode.Read, true);
            using var actualBytes = CreateArchive(CompressionLevel.NoCompression);
            using var provider = new ZipFileProvider(actualBytes, true);
            if (closed) { zip.Dispose(); provider.Dispose(); }
            var expected = Assert.Throws<ArgumentNullException>(() => zip.GetEntry(null!));
            var actual = Assert.Throws<ArgumentNullException>(() => provider.OpenFile(null!));
            Assert.That(actual!.ParamName, Is.EqualTo(expected!.ParamName));
        }

        [Test]
        public async Task ApplicationMimeCallbackCanReenterTheProvider()
        {
            using var bytes = CreateArchive(CompressionLevel.Optimal);
            using var provider = new ZipFileProvider(bytes, true);
            var mime = new ReentrantMime(() => { using var entry = provider.OpenFile("b.bin"); Assert.That(entry.ReadByte(), Is.EqualTo((int)'b')); });
            var result = await Task.Run(() => provider.MapUrlPath("/a.bin", mime)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(result!.ContentType, Is.EqualTo("application/octet-stream"));
        }

        [TestCase(CompressionLevel.NoCompression)]
        [TestCase(CompressionLevel.Optimal)]
        public void OpeningEntryDoesNotMaterializeItsEntireContents(CompressionLevel compression)
        {
            using var bytes = CreateArchive(compression);
            using var provider = new ZipFileProvider(bytes, true);
            var before = GC.GetAllocatedBytesForCurrentThread();
            using var entry = provider.OpenFile("a.bin");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.LessThan(100000), "Opening a 128KiB entry must not allocate a whole-entry buffer.");
            Assert.That(entry.ReadByte(), Is.EqualTo((int)'a'));
        }

        [Test]
        public async Task IndependentProviderRemainsReadableWhileAnotherArchiveIsPaused()
        {
            using var raw = new PausedArchive(CreateArchive(CompressionLevel.NoCompression));
            using var firstProvider = new ZipFileProvider(raw, true);
            using var secondBytes = CreateArchive(CompressionLevel.Optimal);
            using var secondProvider = new ZipFileProvider(secondBytes, true);
            using var a = firstProvider.OpenFile("a.bin");
            raw.Arm();
            var paused = Task.Run(() => ReadByte(a, false));
            try
            {
                await raw.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var other = Task.Run(() => { using var b = secondProvider.OpenFile("b.bin"); return b.ReadByte(); });
                Assert.That(await other.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo((int)'b'));
            }
            finally { raw.Release(); }
            Assert.That(await paused.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo((int)'a'));
        }

        [Test]
        public async Task ProviderDisposalWakesQueuedMetadataWithoutClosingBorrowedInput()
        {
            using var raw = new PausedArchive(CreateArchive(CompressionLevel.NoCompression));
            using var provider = new ZipFileProvider(raw, true);
            using var a = provider.OpenFile("a.bin");
            raw.Arm();
            var reader = Task.Run(() => ReadByte(a, false));
            try
            {
                await raw.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queued = Task.Run(() => provider.MapUrlPath("/b.bin", new ReentrantMime(() => { })));
                await Task.Delay(100);
                provider.Dispose();
                await Assert.ThatAsync(async () => await queued.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<ObjectDisposedException>());
                Assert.That(raw.CanRead, Is.True);
            }
            finally { raw.Release(); }
            Assert.That(await reader.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo((int)'a'));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedReadReleasesTheArchiveForOtherEntries(bool asynchronous)
        {
            using var raw = new PausedArchive(CreateArchive(CompressionLevel.NoCompression));
            using var provider = new ZipFileProvider(raw, true);
            using var a = provider.OpenFile("a.bin");
            using var b = provider.OpenFile("b.bin");
            raw.FailNextRead();
            await Assert.ThatAsync(async () => await ReadByte(a, asynchronous), Throws.InstanceOf<IOException>());
            Assert.That(await ReadByte(b, asynchronous), Is.EqualTo((int)'b'));
            Assert.Throws<FileNotFoundException>(() => provider.OpenFile("missing.bin"));
            Assert.That(await ReadByte(a, asynchronous), Is.EqualTo((int)'a'));
        }

        [TestCase(CompressionLevel.NoCompression)]
        [TestCase(CompressionLevel.Optimal)]
        public async Task FileBackedProviderServesConcurrentReadsAndReleasesItsHandle(CompressionLevel compression)
        {
            var path = Path.Combine(Path.GetTempPath(), "embedio-zip-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var bytes = CreateArchive(compression)) File.WriteAllBytes(path, bytes.ToArray());
                using (var provider = new ZipFileProvider(path))
                {
                    await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
                    {
                        var value = index % 2 == 0 ? 'a' : 'b';
                        using var entry = provider.OpenFile(value + ".bin");
                        using var output = new MemoryStream();
                        await entry.CopyToAsync(output, 4096);
                        Assert.That(output.Length, Is.EqualTo(131072));
                        Assert.That(output.ToArray(), Is.All.EqualTo((byte)value));
                    }));
                }
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.That(exclusive.Length, Is.GreaterThan(0));
            }
            finally { File.Delete(path); }
        }

        private static async Task<string> Outcome(Stream stream, Func<Stream, Task> operation)
        {
            try { await operation(stream); return "success"; }
            catch (Exception error) { return error.GetType().Name + ":" + (error as ArgumentException)?.ParamName; }
        }

        private sealed class ReentrantMime : IMimeTypeProvider
        {
            private readonly Action _callback;
            internal ReentrantMime(Action callback) => _callback = callback;
            public string GetMimeType(string extension) { _callback(); return "application/octet-stream"; }
            public bool TryDetermineCompression(string mimeType, out bool preferCompression) { preferCompression = false; return false; }
        }

        private static async Task<int> ReadByte(Stream stream, bool asynchronous)
        {
            var buffer = new byte[1];
            var count = asynchronous ? await stream.ReadAsync(buffer, 0, 1) : stream.Read(buffer, 0, 1);
            Assert.That(count, Is.EqualTo(1));
            return buffer[0];
        }

        private static MemoryStream CreateArchive(CompressionLevel compression)
        {
            var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var value in new[] { 'a', 'b' })
                {
                    using var entry = zip.CreateEntry(value + ".bin", compression).Open();
                    var bytes = new byte[131072];
                    Array.Fill(bytes, (byte)value);
                    entry.Write(bytes, 0, bytes.Length);
                }
            }
            memory.Position = 0;
            return memory;
        }

        private sealed class PausedArchive : Stream
        {
            private readonly Stream _inner;
            private readonly ManualResetEventSlim _release = new(false);
            private int _armed;
            private int _failNext;
            internal PausedArchive(Stream inner) => _inner = inner;
            internal TaskCompletionSource<bool> Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void FailNextRead() => Volatile.Write(ref _failNext, 1);
            internal void Arm() => Volatile.Write(ref _armed, 1);
            internal void Release() => _release.Set();
            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    Paused.TrySetResult(true);
                    if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Paused archive read did not resume.");
                }
                if (Interlocked.Exchange(ref _failNext, 0) == 1) throw new IOException("Injected read failure.");
                return _inner.Read(buffer, offset, count);
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Task.Run(() => Read(buffer, offset, count), cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void Flush() => _inner.Flush();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Release(); _inner.Dispose(); _release.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
