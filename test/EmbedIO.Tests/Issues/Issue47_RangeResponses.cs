using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #47 evaluates reusable opt-in range responses beyond FileModule. The
    // existing SendRepresentationAsync helper is that facility; these cases pin
    // the acceptance criteria end to end over real HTTP/1.1 and HTTP/2 GET
    // requests from an ordinary action module, outside FileModule.
    public class Issue47_RangeResponses
    {
        private const string Tag = "\"rev-7\"";
        private static readonly DateTimeOffset Modified = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        private static readonly byte[] Payload = Enumerable.Range(0, 100_000).Select(i => (byte)('a' + i % 26)).ToArray();
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [TestCase(1, "bytes=10-19", 10, 19)]
        [TestCase(2, "bytes=10-19", 10, 19)]
        [TestCase(1, "bytes=-5", 99_995, 99_999)]
        [TestCase(2, "bytes=-5", 99_995, 99_999)]
        [TestCase(1, "bytes=99990-", 99_990, 99_999)]
        [TestCase(2, "bytes=99990-", 99_990, 99_999)]
        [TestCase(1, "bytes=0-199999", 0, 99_999)]
        public async Task ValidSuffixAndOpenEndedRangesReturnPartialContent(int protocol, string range, int start, int end)
        {
            await using var host = await Host.StartAsync(Serve());
            using var client = Host.CreateClient(protocol);
            using var response = await client.SendAsync(Request(client, host.Url, range));
            Assert.That(response.Version.Major, Is.EqualTo(protocol));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(response.Headers.AcceptRanges, Does.Contain("bytes"));
            Assert.That(response.Headers.ETag?.Tag, Is.EqualTo(Tag));
            Assert.That(response.Content.Headers.ContentRange?.ToString(), Is.EqualTo($"bytes {start}-{end}/{Payload.Length}"));
            Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(end - start + 1));
            Assert.That(response.Content.Headers.ContentEncoding, Is.Empty, "Ranges address the stored bytes; no coding is applied.");
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Payload.Skip(start).Take(end - start + 1).ToArray()));
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task UnsatisfiableRangeReturns416WithTotalLength(int protocol)
        {
            var source = new TrackingStream(Payload);
            await using var host = await Host.StartAsync(context => context.SendRepresentationAsync(source, "application/octet-stream", Tag, Modified));
            using var client = Host.CreateClient(protocol);
            using var response = await client.SendAsync(Request(client, host.Url, "bytes=100000-100010"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestedRangeNotSatisfiable));
            Assert.That(response.Content.Headers.ContentRange?.ToString(), Is.EqualTo($"bytes */{Payload.Length}"));
            Assert.That(source.Disposed, Is.True, "An owned source is released even when nothing is sent.");
            Assert.That(source.BytesRead, Is.Zero);
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task IfRangeSelectsBetweenPartialAndFullResponses(int protocol)
        {
            await using var host = await Host.StartAsync(Serve());
            using var client = Host.CreateClient(protocol);
            using (var matching = await client.SendAsync(Request(client, host.Url, "bytes=0-9", ("If-Range", Tag))))
            {
                Assert.That(matching.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                Assert.That(await matching.Content.ReadAsByteArrayAsync(), Is.EqualTo(Payload.Take(10).ToArray()));
            }
            using (var stale = await client.SendAsync(Request(client, host.Url, "bytes=0-9", ("If-Range", "\"rev-6\""))))
            {
                Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.OK), "A failed If-Range sends the whole representation.");
                Assert.That(stale.Content.Headers.ContentLength, Is.EqualTo(Payload.Length));
                Assert.That(stale.Content.Headers.ContentRange, Is.Null);
                Assert.That(await stale.Content.ReadAsByteArrayAsync(), Is.EqualTo(Payload));
            }
            using (var weakDate = await client.SendAsync(Request(client, host.Url, "bytes=0-9", ("If-Range", Modified.ToString("r")))))
                Assert.That(weakDate.StatusCode, Is.EqualTo(HttpStatusCode.OK), "A Last-Modified date is weak unless declared strong.");
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task ConditionalRequestsRunBeforeRangeSelection(int protocol)
        {
            var source = new TrackingStream(Payload);
            await using var host = await Host.StartAsync(context => context.SendRepresentationAsync(source, "application/octet-stream", Tag, Modified, leaveOpen: true));
            using var client = Host.CreateClient(protocol);
            using (var notModified = await client.SendAsync(Request(client, host.Url, "bytes=0-9", ("If-None-Match", Tag))))
            {
                Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                Assert.That(notModified.Headers.ETag?.Tag, Is.EqualTo(Tag));
                Assert.That(await notModified.Content.ReadAsByteArrayAsync(), Is.Empty);
            }
            using (var failed = await client.SendAsync(Request(client, host.Url, "bytes=0-9", ("If-Match", "\"rev-6\""))))
            {
                Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.PreconditionFailed));
                Assert.That(failed.Content.Headers.ContentLength, Is.EqualTo(0));
            }
            using (var unmodified = await client.SendAsync(Request(client, host.Url, "bytes=0-9", ("If-Modified-Since", Modified.ToString("r")))))
                Assert.That(unmodified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(source.BytesRead, Is.Zero, "Preconditions decide without reading the source.");
            Assert.That(source.Disposed, Is.False, "leaveOpen keeps the caller's source.");
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task HeadSendsMetadataAndOtherMethodsIgnoreRange(int protocol)
        {
            var source = new TrackingStream(Payload);
            await using var host = await Host.StartAsync(context => context.SendRepresentationAsync(source, "application/octet-stream", Tag, Modified, leaveOpen: true), HttpVerbs.Any);
            using var client = Host.CreateClient(protocol);
            using (var head = await client.SendAsync(Request(client, host.Url, "bytes=0-9", method: HttpMethod.Head)))
            {
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(Payload.Length));
                Assert.That(head.Headers.AcceptRanges, Does.Contain("bytes"));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
            }
            Assert.That(source.BytesRead, Is.Zero, "HEAD never reads the source.");
            using var post = await client.SendAsync(Request(client, host.Url, "bytes=0-9", method: HttpMethod.Post));
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Range applies to GET only.");
            Assert.That(post.Content.Headers.ContentRange, Is.Null);
            Assert.That(await post.Content.ReadAsByteArrayAsync(), Is.EqualTo(Payload));
        }

        [Test]
        public async Task AcceptEncodingDoesNotChangeRangeBytes()
        {
            await using var host = await Host.StartAsync(Serve());
            using var client = Host.CreateClient(1);
            var request = Request(client, host.Url, "bytes=100-199");
            request.Headers.AcceptEncoding.ParseAdd("gzip, br");
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(response.Content.Headers.ContentEncoding, Is.Empty, "The helper never negotiates a coding; ranges are stored-byte offsets.");
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Payload.Skip(100).Take(100).ToArray()));
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task UnknownLengthSourceIsRejectedBeforeCommitment(int protocol)
        {
            // Select a detailed replacement explicitly; Neo v2's default 500 is empty.
            await using var host = await Host.StartAsync(context => context.SendRepresentationAsync(new ForwardOnlyStream(Payload), "application/octet-stream"),
                onUnhandledException: ExceptionHandler.HtmlResponse);
            using var client = Host.CreateClient(protocol);
            using var response = await client.SendAsync(Request(client, host.Url, "bytes=0-9"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError), "A non-seekable source has no known length; the exception handler still owns the response.");
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("seekable"));
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task LargeRangeStreamsInBoundedReadsWithoutBuffering(int protocol)
        {
            const long total = 64L * 1024 * 1024;
            var source = new PatternStream(total);
            await using var host = await Host.StartAsync(context => context.SendRepresentationAsync(source, "application/octet-stream", Tag));
            using var client = Host.CreateClient(protocol);
            using var deadline = new CancellationTokenSource(Deadline);
            const long start = 1_000_003, end = 9_000_002;
            using var response = await client.SendAsync(Request(client, host.Url, $"bytes={start}-{end}"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(end - start + 1));
            using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[81920];
            long position = start, received = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, 0, buffer.Length, deadline.Token)) > 0)
            {
                for (var i = 0; i < read; i++)
                    if (buffer[i] != PatternStream.ByteAt(position + i)) Assert.Fail($"Byte mismatch at offset {position + i}.");
                position += read; received += read;
            }
            Assert.That(received, Is.EqualTo(end - start + 1));
            Assert.That(source.LargestRead, Is.LessThanOrEqualTo(65536), "Reads stay within the copy buffer; the range is never materialized.");
            Assert.That(source.BytesRead, Is.EqualTo(end - start + 1), "Only the selected range is read.");
            // The server releases the source after its last write completes, which can follow the client reading the final byte.
            await source.DisposedSignal.Task.WaitAsync(Deadline);
            Assert.That(source.Disposed, Is.True);
        }

        [Test]
        public async Task ClientDisconnectDuringRangeReleasesTheOwnedSource()
        {
            var source = new PatternStream(64L * 1024 * 1024) { Pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var host = await Host.StartAsync(context => context.SendRepresentationAsync(source, "application/octet-stream"), ignoreWriteExceptions: false);
            var address = new Uri(host.Url);
            using (var socket = new System.Net.Sockets.TcpClient())
            {
                await socket.ConnectAsync(IPAddress.Loopback, address.Port);
                var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {address.Authority}\r\nRange: bytes=0-\r\n\r\n");
                await socket.GetStream().WriteAsync(request, 0, request.Length);
                var first = new byte[4096];
                Assert.That(await socket.GetStream().ReadAsync(first, 0, first.Length).WaitAsync(Deadline), Is.GreaterThan(0));
            }
            // The client is gone; let the copy loop continue until the transport fails.
            source.Pause.TrySetResult();
            await source.DisposedSignal.Task.WaitAsync(Deadline);
            Assert.That(source.Disposed, Is.True, "The owned source is released after the transport failure.");
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        private static Func<IHttpContext, Task> Serve() => context => context.SendRepresentationAsync(new MemoryStream(Payload, false), "application/octet-stream", Tag, Modified);

        // A hand-built message defaults to HTTP/1.1 and would override the client's
        // HTTP/2 default, so the message takes the client's version and policy.
        private static HttpRequestMessage Request(HttpClient client, string url, string range, (string Name, string Value)? header = null, HttpMethod? method = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, url)
            {
                Version = client.DefaultRequestVersion,
                VersionPolicy = client.DefaultVersionPolicy,
            };
            request.Headers.TryAddWithoutValidation("Range", range);
            if (header.HasValue) request.Headers.TryAddWithoutValidation(header.Value.Name, header.Value.Value);
            return request;
        }

        private sealed class TrackingStream : MemoryStream
        {
            public TrackingStream(byte[] bytes) : base(bytes, false) { }
            public long BytesRead { get; private set; }
            public bool Disposed { get; private set; }
            public override int Read(byte[] buffer, int offset, int count) { var n = base.Read(buffer, offset, count); BytesRead += n; return n; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                var n = base.Read(buffer, offset, count); BytesRead += n; return Task.FromResult(n);
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        // Readable and seekable without holding its content: byte i is a function of i.
        private sealed class PatternStream : Stream
        {
            private readonly long _length;
            private long _position;
            public PatternStream(long length) { _length = length; }
            public static byte ByteAt(long position) => (byte)(position * 31 % 251);
            public long BytesRead { get; private set; }
            public int LargestRead { get; private set; }
            public bool Disposed { get; private set; }
            public TaskCompletionSource? Pause { get; set; }
            public TaskCompletionSource DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _position; set => _position = value; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = (int)Math.Min(count, _length - _position);
                for (var i = 0; i < n; i++) buffer[offset + i] = ByteAt(_position + i);
                _position += n; BytesRead += n; LargestRead = Math.Max(LargestRead, n);
                return n;
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Pause != null && _position > 1_000_000) await Pause.Task.WaitAsync(cancellationToken);
                return Read(buffer, offset, count);
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => _length + offset };
                return _position;
            }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { Disposed = true; DisposedSignal.TrySetResult(); base.Dispose(disposing); }
        }

        private sealed class ForwardOnlyStream : Stream
        {
            private readonly MemoryStream _inner;
            public ForwardOnlyStream(byte[] bytes) { _inner = new MemoryStream(bytes, false); }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;
            private Host(WebServer server, string url) { _server = server; Url = url; _running = server.RunAsync(_stop.Token); }
            internal string Url { get; }

            internal static Task<Host> StartAsync(Func<IHttpContext, Task> handler, HttpVerbs verbs = HttpVerbs.Get, bool ignoreWriteExceptions = true,
                ExceptionHandlerCallback? onUnhandledException = null)
            {
                var url = Resources.GetServerAddress();
                var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                    .WithModule(new ActionModule("/", verbs, context => context.Request.Url.AbsolutePath == "/probe"
                        ? context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)
                        : handler(context)));
                server.Listener.IgnoreWriteExceptions = ignoreWriteExceptions;
                if (onUnhandledException != null) server.HandleUnhandledException(onUnhandledException);
                return Task.FromResult(new Host(server, url));
            }

            internal static HttpClient CreateClient(int protocol) => new(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None })
            {
                DefaultRequestVersion = protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = Deadline,
            };

            internal async Task<string> ProbeAsync()
            {
                using var client = CreateClient(1);
                return await client.GetStringAsync(Url + "probe");
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(Deadline); }
                finally { _server.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
