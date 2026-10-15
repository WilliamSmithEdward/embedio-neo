using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RepresentationStreamingTest
    {
        public class ResponseProxy : DispatchProxy, IDisposable
        {
            internal readonly WebHeaderCollection Fields = new();
            internal readonly Dictionary<string, object?> Values = new()
            { ["StatusCode"] = 200, ["ContentLength64"] = -1L, ["SendChunked"] = false };
            internal Stream Output = new MemoryStream();
            public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
            protected virtual void Dispose(bool disposing) { if (disposing) Output.Dispose(); }
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                var name = targetMethod?.Name ?? throw new InvalidOperationException("Missing member.");
                if (name == "get_Headers") return Fields;
                if (name == "get_OutputStream") return Output;
                if (name.StartsWith("set_", StringComparison.Ordinal))
                { Values[name.Substring(4)] = args?[0]; return null; }
                if (name.StartsWith("get_", StringComparison.Ordinal) && Values.TryGetValue(name.Substring(4), out var value)) return value;
                throw new InvalidOperationException("Unexpected response access: " + name);
            }
        }
        public class ContextProxy : DispatchProxy
        {
            internal IHttpRequest? Request;
            internal IHttpResponse? Response;
            internal CancellationToken Token;
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
                => targetMethod?.Name switch
                {
                    "get_Request" => Request,
                    "get_Response" => Response,
                    "get_CancellationToken" => Token,
                    _ => throw new InvalidOperationException("Unexpected context access.")
                };
        }
        private static (IHttpContext Context, ResponseProxy Response, RequestPreconditionsTest.RequestProxy Request) Create(string method, string? range, CancellationToken token = default)
        {
            var request = DispatchProxy.Create<IHttpRequest, RequestPreconditionsTest.RequestProxy>();
            var requestState = (RequestPreconditionsTest.RequestProxy)(object)request;
            requestState.Method = method;
            if (range != null) requestState.Fields["Range"] = range;
            var response = DispatchProxy.Create<IHttpResponse, ResponseProxy>();
            var context = DispatchProxy.Create<IHttpContext, ContextProxy>();
            var state = (ContextProxy)(object)context;
            state.Request = request; state.Response = response; state.Token = token;
            return (context, (ResponseProxy)(object)response, requestState);
        }
        private static byte[] Bytes(ResponseProxy response) => ((MemoryStream)response.Output).ToArray();
        [TestCase("GET")]
        [TestCase("QUERY")]
        public async Task MultipartFramingMatchesDeclaredLengthAndRequestedOrder(string method)
        {
            var fixture = Create(method, "bytes=8-9,0-1");
            using var responseOwner = fixture.Response;
            using var content = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
            await fixture.Context.SendRepresentationAsync(content, "text/plain", "\"v\"", leaveOpen: true);
            var type = (string)(fixture.Response.Values["ContentType"] ?? throw new AssertionException("Missing media type."));
            Assert.That(type, Does.StartWith("multipart/byteranges; boundary="));
            var boundary = type.Substring(type.IndexOf('=', StringComparison.Ordinal) + 1);
            var expected = "--" + boundary + "\r\nContent-Type: text/plain\r\nContent-Range: bytes 8-9/10\r\n\r\n89\r\n--"
                + boundary + "\r\nContent-Type: text/plain\r\nContent-Range: bytes 0-1/10\r\n\r\n01\r\n--" + boundary + "--\r\n";
            Assert.That(Encoding.ASCII.GetString(Bytes(fixture.Response)), Is.EqualTo(expected));
            Assert.That(fixture.Response.Values["ContentLength64"], Is.EqualTo((long)Encoding.ASCII.GetByteCount(expected)));
            Assert.That(fixture.Response.Values["StatusCode"], Is.EqualTo(206));
            Assert.That(fixture.Response.Fields["Content-Range"], Is.Null);
            Assert.That(content.CanRead, Is.True);
        }
        [TestCase("bytes=0-1,2-3", "0123", "bytes 0-3/10")]
        [TestCase("bytes=0-1,8-9,2-7", "0123456789", "bytes 0-9/10")]
        [TestCase("bytes=99-,3-4", "34", "bytes 3-4/10")]
        [TestCase("bytes=,3-4,,", "34", "bytes 3-4/10")]
        [TestCase("bytes=-0,-2", "89", "bytes 8-9/10")]
        public async Task CoalescedAndMixedSatisfiabilityRangesUseSinglePart(string ranges, string expected, string metadata)
        {
            var fixture = Create("GET", ranges);
            using var responseOwner = fixture.Response;
            using var content = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
            await fixture.Context.SendRepresentationAsync(content, "text/plain", leaveOpen: true);
            Assert.That(Encoding.ASCII.GetString(Bytes(fixture.Response)), Is.EqualTo(expected));
            Assert.That(fixture.Response.Fields["Content-Range"], Is.EqualTo(metadata));
            Assert.That(fixture.Response.Values["ContentType"], Is.EqualTo("text/plain"));
        }
        [TestCase("items=0-1,8-9")]
        [TestCase("bytes=0-1,wrong")]
        [TestCase("bytes=3-1,8-9")]
        public async Task InvalidRangeFallsBackToEntireRepresentation(string range)
        {
            var fixture = Create("GET", range);
            using var responseOwner = fixture.Response;
            using var content = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
            await fixture.Context.SendRepresentationAsync(content, "text/plain", leaveOpen: true);
            Assert.That(fixture.Response.Values["StatusCode"], Is.EqualTo(200));
            Assert.That(Encoding.ASCII.GetString(Bytes(fixture.Response)), Is.EqualTo("0123456789"));
        }
        [Test]
        public async Task RangeBudgetCountsUnsatisfiableSpecifications()
        {
            var fixture = Create("QUERY", "bytes=99-,98-,0-1");
            using var responseOwner = fixture.Response;
            using var content = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
            await fixture.Context.SendRepresentationAsync(content, "text/plain", leaveOpen: true, maximumRanges: 2);
            Assert.That(fixture.Response.Values["StatusCode"], Is.EqualTo(200));
            Assert.That(Bytes(fixture.Response).Length, Is.EqualTo(10));
        }
        [TestCase("HEAD")]
        [TestCase("POST")]
        [TestCase("query")]
        public async Task OtherMethodsIgnoreRangeAndHeadSendsOnlyMetadata(string method)
        {
            var fixture = Create(method, "bytes=0-1,8-9");
            using var responseOwner = fixture.Response;
            using var content = new MemoryStream(new byte[10]);
            await fixture.Context.SendRepresentationAsync(content, "application/octet-stream", leaveOpen: true);
            Assert.That(fixture.Response.Values["StatusCode"], Is.EqualTo(200));
            Assert.That(fixture.Response.Values["ContentLength64"], Is.EqualTo(10L));
            Assert.That(Bytes(fixture.Response).Length, Is.EqualTo(method == "HEAD" ? 0 : 10));
        }
        [TestCase("If-None-Match", "\"v\"", 304)]
        [TestCase("If-Match", "\"other\"", 412)]
        public async Task PreconditionsRunBeforeUnsatisfiableRangeAndReleaseOwnedSource(string field, string value, int status)
        {
            var fixture = Create("QUERY", "bytes=99-");
            using var responseOwner = fixture.Response;
            fixture.Request.Fields[field] = value;
            fixture.Response.Fields["Content-Encoding"] = "gzip";
            fixture.Response.Fields["Content-Range"] = "bytes 0-1/10";
            var content = new MemoryStream(new byte[10]);
            await fixture.Context.SendRepresentationAsync(content, "application/octet-stream", "\"v\"");
            Assert.That(fixture.Response.Values["StatusCode"], Is.EqualTo(status));
            Assert.That(fixture.Response.Fields["Content-Range"], Is.Null);
            Assert.That(fixture.Response.Fields["Content-Encoding"], Is.EqualTo(status == 304 ? "gzip" : null));
            Assert.That(fixture.Response.Values["ContentLength64"], Is.EqualTo(status == 304 ? -1L : 0L));
            Assert.That(Bytes(fixture.Response), Is.Empty);
            Assert.That(content.CanRead, Is.False);
        }
        [Test]
        public async Task UnsatisfiableRangesDisposeSourceAndPreserve416Length()
        {
            var fixture = Create("GET", "bytes=99-,100-");
            using var responseOwner = fixture.Response;
            var content = new MemoryStream(new byte[10]);
            fixture.Response.Fields["Content-Encoding"] = "gzip";
            var error = await Assert.ThrowsAsync<HttpRangeNotSatisfiableException>(() => fixture.Context.SendRepresentationAsync(content, "text/plain"));
            Assert.That(error?.StatusCode, Is.EqualTo(416));
            Assert.That(error?.ContentLength, Is.EqualTo(10));
            Assert.That(fixture.Response.Fields["Content-Encoding"], Is.Null);
            Assert.That(content.CanRead, Is.False);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task PreCanceledRequestHonorsSourceOwnership(bool leaveOpen)
        {
            using var stop = new CancellationTokenSource(); stop.Cancel();
            var fixture = Create("GET", null, stop.Token);
            using var responseOwner = fixture.Response;
            using var content = new MemoryStream(new byte[10]);
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Context.SendRepresentationAsync(content, "text/plain", leaveOpen: leaveOpen));
            Assert.That(content.CanRead, Is.EqualTo(leaveOpen));
            Assert.That(Bytes(fixture.Response), Is.Empty);
        }
        [TestCase(0)]
        [TestCase(129)]
        public async Task InvalidBudgetDoesNotAcceptOwnership(int maximum)
        {
            var fixture = Create("GET", null);
            using var responseOwner = fixture.Response;
            using var content = new MemoryStream(new byte[10]);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Context.SendRepresentationAsync(content, "text/plain", maximumRanges: maximum));
            Assert.That(content.CanRead, Is.True);
        }
        private sealed class TrackedSource : MemoryStream
        {
            internal int Reads;
            internal long? DeclaredLength;
            internal TrackedSource(byte[] bytes) : base(bytes) { }
            public override long Length => DeclaredLength ?? base.Length;
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { Reads++; return base.ReadAsync(buffer, offset, count, token); }
        }
        private sealed class PausedSink : MemoryStream
        {
            internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task WriteAsync(byte[] bytes, int offset, int count, CancellationToken token)
            {
                Entered.TrySetResult(true);
                await Continue.Task.WaitAsync(token);
                await base.WriteAsync(bytes, offset, count, token);
            }
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task MultipartBackpressureAndCancellationDoNotReadAhead(bool cancel)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var fixture = Create("QUERY", "bytes=0-99999,150000-199999", stop.Token);
            using var responseOwner = fixture.Response;
            fixture.Response.Output.Dispose();
            var sink = new PausedSink(); fixture.Response.Output = sink;
            var source = new TrackedSource(new byte[200000]);
            var sending = fixture.Context.SendRepresentationAsync(source, "application/octet-stream");
            await sink.Entered.Task.WaitAsync(stop.Token);
            Assert.That(source.Reads, Is.Zero, "Blocked multipart header writes must prevent reading source data ahead.");
            Assert.That(sending.IsCompleted, Is.False);
            if (cancel)
            {
                stop.Cancel();
                await Assert.ThrowsAsync<TaskCanceledException>(() => sending);
            }
            else
            {
                sink.Continue.TrySetResult(true); await sending.WaitAsync(stop.Token);
                Assert.That(source.Reads, Is.EqualTo(3), "Each range is copied in bounded 64 KiB source reads.");
                Assert.That(sink.Length, Is.EqualTo(fixture.Response.Values["ContentLength64"]));
            }
            Assert.That(source.CanRead, Is.False);
        }
        [Test]
        public async Task TruncatedRepresentationFailsAndClosesOwnedSource()
        {
            var fixture = Create("GET", null);
            using var responseOwner = fixture.Response;
            var source = new TrackedSource(new byte[2]) { DeclaredLength = 10 };
            await Assert.ThrowsAsync<EndOfStreamException>(() => fixture.Context.SendRepresentationAsync(source, "application/octet-stream"));
            Assert.That(source.CanRead, Is.False);
            Assert.That(Bytes(fixture.Response).Length, Is.EqualTo(2));
        }
        [Test]
        public async Task NotModifiedAndHeadNeverReadOrSeekRepresentation()
        {
            foreach (var method in new[] { "GET", "HEAD" })
            {
                var fixture = Create(method, "bytes=0-1,8-9");
                using var responseOwner = fixture.Response;
                if (method == "GET") fixture.Request.Fields["If-None-Match"] = "\"v\"";
                using var source = new TrackedSource(new byte[10]); source.Position = 7;
                await fixture.Context.SendRepresentationAsync(source, "application/octet-stream", "\"v\"", leaveOpen: true);
                Assert.That(source.Position, Is.EqualTo(7));
                Assert.That(source.Reads, Is.Zero);
                Assert.That(Bytes(fixture.Response), Is.Empty);
            }
        }
    }
}
