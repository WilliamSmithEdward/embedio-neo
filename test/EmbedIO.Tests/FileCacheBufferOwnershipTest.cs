using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    public class FileCacheBufferOwnershipTest
    {
        [TestCase(0, 1, 0)]
        [TestCase(17, 2, 0)]
        [TestCase(65536, 65536, 0)]
        [TestCase(65535, 8192, 0)]
        [TestCase(65537, 8192, 0)]
        [TestCase(65536, 2048, -1)]
        [TestCase(65535, 8192, 1)]
        public async Task ColdAndWarmCopiesPreserveActualBytes(int length, int chunk, int metadataDelta)
        {
            var expected = Payload(length);
            var input = (byte[])expected.Clone();
            var provider = new RetainingProvider(input, length + metadataDelta, chunk);
            using var cache = new FileCache();
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider) { Cache = cache }), async client =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await CheckBody(client, expected, deadline.Token);
                Assert.That(provider.CopyCalls, Is.EqualTo(1));
                Assert.That(provider.Last?.Disposed, Is.True);
                Assert.That(provider.Last?.Destination is MemoryStream, Is.False);
                Array.Fill(input, (byte)255);
                await CheckBody(client, expected, deadline.Token);
                Assert.That(provider.OpenCalls, Is.EqualTo(1), "Warm content must remain isolated from provider-owned input.");
            });
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task RetainedDestinationCannotMutatePublishedCache(int writeKind)
        {
            var expected = Payload(65536);
            var provider = new RetainingProvider(expected, expected.Length, expected.Length);
            using var cache = new FileCache();
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider) { Cache = cache }), async client =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await CheckBody(client, expected, deadline.Token);
                var destination = provider.Last?.Destination ?? throw new AssertionException("Copy must receive a destination.");
                Assert.That(destination.CanRead, Is.False);
                Assert.That(destination.CanSeek, Is.False);
                await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                {
                    var replacement = new byte[] { 255 };
                    switch (writeKind)
                    {
                        case 0:
                            destination.Write(replacement, 0, 1);
                            break;
                        case 1:
                            destination.WriteByte(255);
                            break;
                        case 2:
                            await destination.WriteAsync(replacement, 0, 1, deadline.Token);
                            break;
                        default:
                            await destination.WriteAsync(replacement.AsMemory(), deadline.Token);
                            break;
                    }
                });
                await CheckBody(client, expected, deadline.Token);
                Assert.That(provider.OpenCalls, Is.EqualTo(1));
            });
        }

        [TestCase("gzip")]
        [TestCase("deflate")]
        [TestCase("br")]
        public async Task CompressedCachePreservesFooterAndDecodedContent(string coding)
        {
            ArgumentNullException.ThrowIfNull(coding);
            var expected = Payload(65536);
            var provider = new RetainingProvider(expected, expected.Length, 1024);
            using var cache = new FileCache();
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider) { Cache = cache }), async client =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/sample.bin");
                    request.Headers.TryAddWithoutValidation(HttpHeaderNames.AcceptEncoding, coding + ", identity;q=0");
                    using var response = await client.SendAsync(request, deadline.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { coding }));
                    using var encoded = new MemoryStream(await response.Content.ReadAsByteArrayAsync(deadline.Token));
                    using Stream decoded = coding switch
                    {
                        "gzip" => new GZipStream(encoded, CompressionMode.Decompress),
                        "deflate" => new DeflateStream(encoded, CompressionMode.Decompress),
                        _ => new BrotliStream(encoded, CompressionMode.Decompress),
                    };
                    using var bytes = new MemoryStream();
                    await decoded.CopyToAsync(bytes, deadline.Token);
                    Assert.That(bytes.ToArray(), Is.EqualTo(expected));
                }
                Assert.That(provider.OpenCalls, Is.EqualTo(1));
                Assert.That(provider.Last?.Disposed, Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedOrCanceledCopyDoesNotPublishPartialCache(bool cancel)
        {
            var expected = Payload(65536);
            var provider = new RetainingProvider(expected, expected.Length, 1024);
            Exception? failure = null;
            using var cache = new FileCache();
            using var requestCancellation = new CancellationTokenSource();
            provider.AfterFirstWrite = cancel ? requestCancellation.Cancel : () => throw new IOException("Controlled copy failure.");
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider) { Cache = cache })
                .HandleUnhandledException((context, error) =>
                {
                    failure = error;
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    return Task.CompletedTask;
                }), async client =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var first = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation.Token, deadline.Token);
                if (cancel)
                {
                    await Assert.ThatAsync(async () =>
                    {
                        using var response = await client.GetAsync("/sample.bin", first.Token);
                    }, Throws.InstanceOf<OperationCanceledException>());
                }
                else
                {
                    using var response = await client.GetAsync("/sample.bin", first.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                    Assert.That(failure, Is.TypeOf<IOException>());
                }
                Assert.That(provider.Last?.Disposed, Is.True);
                provider.AfterFirstWrite = null;
                await CheckBody(client, expected, deadline.Token);
                await CheckBody(client, expected, deadline.Token);
                Assert.That(provider.OpenCalls, Is.EqualTo(2), "An incomplete fill must be retried, then cached.");
            });
        }

        [TestCase(65536)]
        [TestCase(65535)]
        public async Task ColdAndWarmRangesKeepExactSlice(int length)
        {
            var expected = Payload(length);
            var provider = new RetainingProvider(expected, length, 2048);
            using var cache = new FileCache();
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider) { Cache = cache }), async client =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/sample.bin");
                    request.Headers.TryAddWithoutValidation(HttpHeaderNames.Range, "bytes=7-23");
                    using var response = await client.SendAsync(request, deadline.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(deadline.Token), Is.EqualTo(expected.Skip(7).Take(17).ToArray()));
                }
                Assert.That(provider.OpenCalls, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SourceDisposalWritesCompleteBeforeCacheExtraction()
        {
            var expected = Payload(65536);
            var provider = new RetainingProvider(expected[..^1], expected.Length, 65535) { AppendOnDispose = expected[^1] };
            using var cache = new FileCache();
            await TestWebServer.UseAsync(server => server.WithModule(new FileModule("/", provider) { Cache = cache }), async client =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await CheckBody(client, expected, deadline.Token);
                await CheckBody(client, expected, deadline.Token);
                Assert.That(provider.OpenCalls, Is.EqualTo(1));
            });
        }

        private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

        private static async Task CheckBody(HttpClient client, byte[] expected, CancellationToken token)
        {
            using var response = await client.GetAsync("/sample.bin", token);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsByteArrayAsync(token), Is.EqualTo(expected));
            Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(expected.Length));
        }

        private sealed class RetainingProvider : IFileProvider
        {
            private readonly byte[] _bytes;
            private readonly long _length;
            private readonly int _chunk;

            internal RetainingProvider(byte[] bytes, long length, int chunk)
            {
                _bytes = bytes;
                _length = length;
                _chunk = chunk;
            }

            internal int OpenCalls { get; private set; }
            internal int CopyCalls { get; private set; }
            internal RetainingStream? Last { get; private set; }
            internal Action? AfterFirstWrite { get; set; }
            internal byte? AppendOnDispose { get; set; }
            public bool IsImmutable => true;
            public event Action<string>? ResourceChanged { add { } remove { } }
            public void Start(CancellationToken cancellationToken) { }
            public MappedResourceInfo? MapUrlPath(string requestPath, IMimeTypeProvider mimeTypeProvider)
                => requestPath == "/sample.bin" ? MappedResourceInfo.ForFile("sample", "sample.bin", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), _length, "application/octet-stream") : null;
            public Stream? OpenFile(string path)
            {
                OpenCalls++;
                return Last = new RetainingStream(this, _bytes, _chunk);
            }
            public IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mimeTypeProvider) => Array.Empty<MappedResourceInfo>();

            internal sealed class RetainingStream : Stream
            {
                private readonly RetainingProvider _owner;
                private readonly byte[] _bytes;
                private readonly int _chunk;
                internal RetainingStream(RetainingProvider owner, byte[] bytes, int chunk)
                {
                    _owner = owner;
                    _bytes = bytes;
                    _chunk = chunk;
                }
                internal Stream? Destination { get; private set; }
                internal bool Disposed { get; private set; }
                public override bool CanRead => !Disposed;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => _bytes.Length;
                public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
                public override void Flush() => throw new NotSupportedException();
                public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("The provider's CopyToAsync override must be used.");
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
                public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(destination);
                    Destination = destination;
                    _owner.CopyCalls++;
                    Assert.That(destination.CanRead, Is.False);
                    Assert.That(destination.CanSeek, Is.False);
                    for (var offset = 0; offset < _bytes.Length; offset += _chunk)
                    {
                        await destination.WriteAsync(_bytes, offset, Math.Min(_chunk, _bytes.Length - offset), cancellationToken);
                        if (offset == 0) _owner.AfterFirstWrite?.Invoke();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    await destination.FlushAsync(cancellationToken);
                }
                protected override void Dispose(bool disposing)
                {
                    if (disposing && !Disposed)
                    {
                        if (_owner.AppendOnDispose is { } value)
                            (Destination ?? throw new AssertionException("A completed copy must retain its destination.")).WriteByte(value);
                        Disposed = true;
                    }
                    base.Dispose(disposing);
                }
            }
        }
    }
}
