using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class FileModuleShortReadTest
    {
        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public async Task ShortReadsTransferTheCompleteSelectedContent(bool seekable, bool partial)
        {
            var payload = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
            var provider = new ShortProvider(payload, payload.Length, seekable);
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider) { ContentCaching = false }), async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/sample.bin");
                if (partial) request.Headers.TryAddWithoutValidation(HttpHeaderNames.Range, "bytes=5-16");
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(partial ? payload.Skip(5).Take(12).ToArray() : payload));
                Assert.That(provider.Last?.Disposed, Is.True, "FileModule owns the opened stream.");
            });
        }

        [TestCase(false, 100, 105, 200)]
        [TestCase(false, 0, 9, 20)]
        [TestCase(true, 0, 9, 20)]
        [TestCase(false, 0, 99999, 100001)]
        public async Task UnexpectedEndOfFileFailsOnceAndReleasesSource(bool seekable, int start, int end, int declaredLength)
        {
            var provider = new ShortProvider(new byte[] { 1, 2, 3, 4 }, declaredLength, seekable);
            Exception? failure = null;
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider) { ContentCaching = false })
                .HandleUnhandledException((_, error) => { failure = error; return Task.CompletedTask; }), async client =>
            {
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Get, "/sample.bin");
                request.Headers.TryAddWithoutValidation(HttpHeaderNames.Range, $"bytes={start}-{end}");
                using var response = await client.SendAsync(request, watchdog.Token);
                Assert.That(failure, Is.TypeOf<EndOfStreamException>());
                Assert.That(provider.Last?.ZeroReads, Is.EqualTo(1), "EOF must terminate both skipping and transfer loops.");
                Assert.That(provider.Last?.Disposed, Is.True);
            });
        }

        private sealed class ShortProvider : IFileProvider
        {
            private readonly byte[] _bytes;
            private readonly long _length;
            private readonly bool _seekable;
            internal ShortProvider(byte[] bytes, long length, bool seekable) { _bytes = bytes; _length = length; _seekable = seekable; }
            internal ShortStream? Last { get; private set; }
            public bool IsImmutable => true;
            public event Action<string>? ResourceChanged { add { } remove { } }
            public void Start(CancellationToken cancellationToken) { }
            public MappedResourceInfo? MapUrlPath(string requestPath, IMimeTypeProvider mimeTypeProvider)
                => requestPath == "/sample.bin" ? MappedResourceInfo.ForFile("sample", "sample.bin", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), _length, "application/octet-stream") : null;
            public Stream? OpenFile(string path) => Last = new ShortStream(_bytes, _seekable);
            public IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mimeTypeProvider) => Array.Empty<MappedResourceInfo>();
        }

        private sealed class ShortStream : Stream
        {
            private readonly MemoryStream _inner;
            private readonly bool _seekable;
            private readonly CancellationTokenSource _watchdog = new(TimeSpan.FromSeconds(5));
            internal ShortStream(byte[] bytes, bool seekable) { _inner = new MemoryStream(bytes, false); _seekable = seekable; }
            internal bool Disposed { get; private set; }
            internal int ZeroReads { get; private set; }
            public override bool CanRead => !Disposed;
            public override bool CanSeek => _seekable;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set { if (!_seekable) throw new NotSupportedException(); _inner.Position = value; } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                _watchdog.Token.ThrowIfCancellationRequested();
                var read = _inner.Read(buffer, offset, Math.Min(count, 2));
                if (count != 0 && read == 0 && ++ZeroReads > 2)
                    throw new IOException("Regression guard: caller repeatedly read a completed stream.");
                return read;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Disposed = true; _inner.Dispose(); _watchdog.Dispose(); }
                base.Dispose(disposing);
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => _seekable ? _inner.Seek(offset, origin) : throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
