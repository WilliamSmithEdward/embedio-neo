using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.Files;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue170_SuffixRanges
    {
        public static IEnumerable SuffixCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO })
                foreach (var cached in new[] { false, true })
                    foreach (var zip in new[] { false, true })
                        foreach (var item in new (long Suffix, int Length, int Start, int Status)[]
                        {
                            (1, 10, 9, 206), (3, 10, 7, 206), (10, 10, 0, 206),
                            (11, 10, 0, 206), (long.MaxValue, 10, 0, 206),
                            (0, 10, 0, 416), (1, 0, 0, 200), (0, 0, 0, 416),
                            (3, 1, 0, 206), (1, 1, 0, 206),
                        })
                            yield return new object[] { mode, cached, zip, item.Suffix, item.Length, item.Start, item.Status };
        }

        public static IEnumerable ProviderCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO })
                foreach (var cached in new[] { false, true })
                    foreach (var zip in new[] { false, true })
                        yield return new object[] { mode, cached, zip };
        }

        [TestCaseSource(nameof(SuffixCases))]
        public async Task SuffixSelectsTailAndPreservesHealthyRequests(HttpListenerMode mode, bool cached, bool zip, long suffix, int length, int start, int status)
        {
            var bytes = Encoding.ASCII.GetBytes("0123456789")[..length];
            await WithServer(mode, cached, zip, bytes, async client =>
            {
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "file.txt");
                    request.Headers.Range = new RangeHeaderValue(null, suffix);
                    using var response = await client.SendAsync(request);
                    Assert.That((int)response.StatusCode, Is.EqualTo(status));
                    if (status == 206)
                    {
                        Assert.That(((response).Content.Headers.ContentRange ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).ToString(), Is.EqualTo($"bytes {start}-{length - 1}/{length}"));
                        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes[start..]));
                    }
                    else if (status == 200)
                    {
                        Assert.That(response.Content.Headers.ContentRange, Is.Null);
                        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
                    }
                    else
                        Assert.That(((response).Content.Headers.ContentRange ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).ToString(), Is.EqualTo($"bytes */{length}"));
                }
                using var full = await client.GetAsync("file.txt");
                Assert.That(full.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(full.Content.Headers.ContentRange, Is.Null);
                Assert.That(await full.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
            });
        }

        [TestCaseSource(nameof(ProviderCases))]
        public async Task HeadValidatorsAndExistingRangePoliciesArePreserved(HttpListenerMode mode, bool cached, bool zip)
        {
            var bytes = Encoding.ASCII.GetBytes("0123456789");
            await WithServer(mode, cached, zip, bytes, async client =>
            {
                using var initial = await client.GetAsync("file.txt");
                var tag = initial.Headers.ETag;
                using var headRequest = new HttpRequestMessage(HttpMethod.Head, "file.txt");
                headRequest.Headers.Range = new RangeHeaderValue(null, 3);
                using var head = await client.SendAsync(headRequest);
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(10));
                Assert.That(head.Content.Headers.ContentRange, Is.Null);
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                foreach (var item in new[]
                {
                    (Range: "bytes=-3", Validator: (tag ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).ToString(), Status: 206, Body: "789"),
                    (Range: "bytes=-3", Validator: "\"different\"", Status: 200, Body: "0123456789"),
                    (Range: "bytes=2-5", Validator: (string?)null, Status: 206, Body: "2345"),
                    (Range: "bytes=7-", Validator: (string?)null, Status: 206, Body: "789"),
                    (Range: "bytes=0-1,7-9", Validator: (string?)null, Status: 200, Body: "0123456789"),
                    (Range: "not-a-range", Validator: (string?)null, Status: 200, Body: "0123456789"),
                    (Range: "bytes=0-10", Validator: (string?)null, Status: 416, Body: (string?)null),
                })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "file.txt");
                    request.Headers.TryAddWithoutValidation("Range", item.Range);
                    if (item.Validator != null) request.Headers.TryAddWithoutValidation("If-Range", item.Validator);
                    using var response = await client.SendAsync(request);
                    Assert.That((int)response.StatusCode, Is.EqualTo(item.Status), item.Range);
                    if (item.Body != null) Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(item.Body));
                }
            });
        }

        private static async Task WithServer(HttpListenerMode mode, bool cached, bool zip, byte[] bytes, Func<HttpClient, Task> exercise)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-170-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var archive = directory + ".zip";
            using var stop = new CancellationTokenSource();
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            Task? running = null;
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, "file.txt"), bytes);
                if (zip)
                {
                    ZipFile.CreateFromDirectory(directory, archive);
                    server.WithZipFile("/", archive, m => m.ContentCaching = cached);
                }
                else server.WithStaticFolder("/", directory, false, m => m.ContentCaching = cached);
                running = server.RunAsync(stop.Token);
                using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
                await exercise(client);
            }
            finally
            {
                stop.Cancel();
                if (running != null) await running.WaitAsync(TimeSpan.FromSeconds(10));
                server.Dispose();
                Directory.Delete(directory, true);
                if (zip && File.Exists(archive)) File.Delete(archive);
            }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task NonSeekableLargeResourceSkipsUsingInt64Offsets(HttpListenerMode mode)
        {
            const long length = (long)int.MaxValue + 4;
            using var stop = new CancellationTokenSource();
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new FileModule("/", new LargeProvider(length)) { ContentCaching = false });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var request = new HttpRequestMessage(HttpMethod.Get, url + "large.bin");
                request.Headers.Range = new RangeHeaderValue(null, 3);
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                Assert.That(((response).Content.Headers.ContentRange ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).ToString(), Is.EqualTo($"bytes {length - 3}-{length - 1}/{length}"));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Encoding.ASCII.GetBytes("789")));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        // Synthetic immutable metadata/stream: exercise >2 GiB offsets without allocating a giant file.
        private sealed class LargeProvider(long length) : IFileProvider
        {
            public bool IsImmutable => true;
            public event Action<string>? ResourceChanged { add { } remove { } }
            public void Start(CancellationToken cancellationToken) { }
            public MappedResourceInfo? MapUrlPath(string path, IMimeTypeProvider mime)
                => path == "/large.bin" ? MappedResourceInfo.ForFile(path, "large.bin", new DateTime(2020, 1, 1), length, "application/octet-stream") : null;
            public Stream OpenFile(string path) => new LargeStream(length);
            public IEnumerable<MappedResourceInfo> GetDirectoryEntries(string path, IMimeTypeProvider mime) => Array.Empty<MappedResourceInfo>();
        }

        private sealed class LargeStream(long length) : Stream
        {
            private long _position;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = (int)Math.Min(count, length - _position);
                // Skipped bytes need not be materialized; only the final requested slice is observed.
                if (_position >= length - 3)
                    for (var i = 0; i < read; i++) buffer[offset + i] = (byte)('7' + _position + i - (length - 3));
                _position += read;
                return read;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
