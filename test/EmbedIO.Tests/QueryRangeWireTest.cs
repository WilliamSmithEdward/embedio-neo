using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class QueryRangeWireTest
    {
        internal static async Task Respond(IHttpContext context, bool gzip)
        {
            var query = await context.GetRequestBodyAsStringAsync();
            var bytes = Encoding.UTF8.GetBytes("selected:" + query + new string('x', 256));
            if (gzip)
            {
                using var output = new MemoryStream();
                using (var encoder = new GZipStream(output, CompressionLevel.SmallestSize, true)) encoder.Write(bytes);
                bytes = output.ToArray();
            }
            var tag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)) + "\"";
            context.Response.Headers[HttpHeaderNames.ETag] = tag;
            context.Response.Headers[HttpHeaderNames.AcceptRanges] = "bytes";
            var status = context.Request.EvaluatePreconditions(tag, null);
            if (status.HasValue)
            {
                if (gzip && status == HttpStatusCode.NotModified) context.Response.Headers[HttpHeaderNames.ContentEncoding] = "gzip";
                context.Response.StatusCode = (int)status.GetValueOrDefault();
                return;
            }
            var selected = context.Request.TryGetByteRange(bytes.LongLength, tag, null, out var start, out var count);
            if (gzip) context.Response.Headers[HttpHeaderNames.ContentEncoding] = "gzip";
            if (selected)
            {
                context.Response.StatusCode = 206;
                context.Response.Headers[HttpHeaderNames.ContentRange] = FormattableString.Invariant($"bytes {start}-{start + count - 1}/{bytes.LongLength}");
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = count;
                await context.Response.OutputStream.WriteAsync(bytes.AsMemory((int)start, (int)count));
            }
            else
            {
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = bytes.LongLength;
                await context.Response.OutputStream.WriteAsync(bytes.AsMemory());
            }
        }
        internal static async Task Verify(HttpClient client, string url, Version version, CancellationToken token, bool nativeWindows = false)
        {
            async Task<HttpResponseMessage> Send(string? range, string? ifRange = null, string? none = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod("QUERY"), url)
                {
                    Version = version,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new StringContent("one", WebServer.Utf8NoBomEncoding, "text/plain")
                };
                if (range != null) request.Headers.TryAddWithoutValidation("Range", range);
                if (ifRange != null) request.Headers.TryAddWithoutValidation("If-Range", ifRange);
                if (none != null) request.Headers.TryAddWithoutValidation("If-None-Match", none);
                return await client.SendAsync(request, token);
            }
            using var first = await Send(null);
            var bytes = await first.Content.ReadAsByteArrayAsync(token);
            var tag = first.Headers.ETag?.ToString() ?? throw new AssertionException("Missing encoded-representation tag.");
            using var prefix = await Send("bytes=0-3", tag);
            Assert.That(prefix.Version, Is.EqualTo(version));
            Assert.That(prefix.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(await prefix.Content.ReadAsByteArrayAsync(token), Is.EqualTo(bytes.AsSpan(0, 4).ToArray()));
            Assert.That(prefix.Content.Headers.ContentRange?.Length, Is.EqualTo(bytes.LongLength));
            using var clamped = await Send("bytes=0-99999999999999999999999999");
            Assert.That(clamped.StatusCode, Is.EqualTo(nativeWindows ? HttpStatusCode.BadRequest : HttpStatusCode.PartialContent));
            if (nativeWindows) Assert.That(clamped.Headers.ETag, Is.Null, "The application would have set a representation tag.");
            else Assert.That(await clamped.Content.ReadAsByteArrayAsync(token), Is.EqualTo(bytes));
            using var boundedEnd = await Send("bytes=0-999999");
            Assert.That(boundedEnd.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(await boundedEnd.Content.ReadAsByteArrayAsync(token), Is.EqualTo(bytes));
            using var suffix = await Send("bytes=-4");
            Assert.That(await suffix.Content.ReadAsByteArrayAsync(token), Is.EqualTo(bytes.AsSpan(bytes.Length - 4).ToArray()));
            using var weak = await Send("bytes=0-3", "W/" + tag);
            Assert.That(weak.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await weak.Content.ReadAsByteArrayAsync(token), Is.EqualTo(bytes));
            using var unchanged = await Send("bytes=0-3", tag, tag);
            Assert.That(unchanged.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(await unchanged.Content.ReadAsByteArrayAsync(token), Is.Empty);
            using var invalid = await Send("bytes=999999-");
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.RequestedRangeNotSatisfiable));
            Assert.That(invalid.Content.Headers.ContentRange?.Length, Is.EqualTo(bytes.LongLength));
        }
        [TestCase(HttpListenerMode.EmbedIO, 1, false)]
        [TestCase(HttpListenerMode.EmbedIO, 2, false)]
        [TestCase(HttpListenerMode.EmbedIO, 1, true)]
        [TestCase(HttpListenerMode.EmbedIO, 2, true)]
        public async Task QueryRangesSelectEncodedResultBytes(HttpListenerMode mode, int protocol, bool gzip)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(mode, url).WithAction("/", HttpVerbs.Query, context => Respond(context, gzip));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None });
                await Verify(client, url, protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11, stop.Token,
                    false);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
    public partial class Http3ListenerTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task QueryRangesPreserveEncodedRepresentationOnHttp3(bool gzip)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(options => options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Query, context => QueryRangeWireTest.Respond(context, gzip));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try { await QueryRangeWireTest.Verify(client, prefix, HttpVersion.Version30, stop.Token); }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
