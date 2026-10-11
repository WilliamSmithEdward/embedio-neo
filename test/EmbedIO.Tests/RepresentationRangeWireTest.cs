using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RepresentationRangeWireTest
    {
        internal static byte[] Payload(bool gzip)
        {
            var bytes = Encoding.ASCII.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
            if (!gzip) return bytes;
            using var result = new MemoryStream();
            using (var encoder = new GZipStream(result, CompressionLevel.SmallestSize, true)) encoder.Write(bytes);
            return result.ToArray();
        }
        internal static Task Respond(IHttpContext context, bool gzip)
        {
            if (gzip) context.Response.Headers["Content-Encoding"] = "gzip";
            return context.SendRepresentationAsync(new MemoryStream(Payload(gzip)), "application/octet-stream", "\"v\"");
        }
        internal static async Task Verify(HttpClient client, string url, Version version, bool gzip, CancellationToken token)
        {
            var bytes = Payload(gzip);
            async Task<HttpResponseMessage> Send(string range, string? condition = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod("QUERY"), url)
                { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new StringContent("select") };
                request.Headers.TryAddWithoutValidation("Range", range);
                if (condition != null) request.Headers.TryAddWithoutValidation("If-None-Match", condition);
                return await client.SendAsync(request, token);
            }
            using var multiple = await Send("bytes=0-3,20-23");
            Assert.That(multiple.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(multiple.Version, Is.EqualTo(version));
            Assert.That(multiple.Content.Headers.ContentRange, Is.Null);
            var type = multiple.Content.Headers.ContentType ?? throw new AssertionException("Missing multipart media type.");
            Assert.That(type.MediaType, Is.EqualTo("multipart/byteranges"));
            string? boundary = null;
            foreach (var parameter in type.Parameters) if (parameter.Name == "boundary") boundary = parameter.Value?.Trim('"');
            Assert.That(boundary, Is.Not.Null);
            using var expected = new MemoryStream();
            foreach (var start in new[] { 0, 20 })
            {
                expected.Write(Encoding.ASCII.GetBytes("--" + boundary + "\r\nContent-Type: application/octet-stream\r\nContent-Range: "
                    + FormattableString.Invariant($"bytes {start}-{start + 3}/{bytes.Length}") + "\r\n\r\n"));
                expected.Write(bytes, start, 4); expected.Write(new byte[] { 13, 10 });
            }
            expected.Write(Encoding.ASCII.GetBytes("--" + boundary + "--\r\n"));
            Assert.That(await multiple.Content.ReadAsByteArrayAsync(token), Is.EqualTo(expected.ToArray()));
            Assert.That(multiple.Content.Headers.ContentLength, Is.EqualTo(expected.Length));
            Assert.That(multiple.Content.Headers.ContentEncoding, Is.EqualTo(gzip ? new[] { "gzip" } : Array.Empty<string>()));
            using var unchanged = await Send("bytes=0-3,20-23", "\"v\"");
            Assert.That(unchanged.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(await unchanged.Content.ReadAsByteArrayAsync(token), Is.Empty);
            using var successor = await Send("bytes=4-7");
            Assert.That(successor.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(await successor.Content.ReadAsByteArrayAsync(token), Is.EqualTo(bytes.AsSpan(4, 4).ToArray()));
        }
        [TestCase(HttpListenerMode.EmbedIO, 1, false)]
        [TestCase(HttpListenerMode.EmbedIO, 2, false)]
        [TestCase(HttpListenerMode.EmbedIO, 1, true)]
        [TestCase(HttpListenerMode.EmbedIO, 2, true)]
        public async Task MultipartQueryRangesStreamSelectedRepresentation(HttpListenerMode mode, int protocol, bool gzip)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(mode, url).WithAction("/", HttpVerbs.Query, context => Respond(context, gzip));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None });
                await Verify(client, url, protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11, gzip, stop.Token);
            }
            catch (Exception error) { TestContext.Error.WriteLine("Multipart request failed before cleanup: " + error); throw; }
            finally { try { stop.Cancel(); } finally { await running.WaitAsync(TimeSpan.FromSeconds(5)); } }
        }
    }
    public partial class Http3ListenerTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task MultipartQueryRangesStreamOnHttp3(bool gzip)
        {
            using var certificate = Certificate(); var prefix = Prefix();
            using var server = new WebServer(options => options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Query, context => RepresentationRangeWireTest.Respond(context, gzip));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try { await RepresentationRangeWireTest.Verify(client, prefix, HttpVersion.Version30, gzip, stop.Token); }
            catch (Exception error) { TestContext.Error.WriteLine("Multipart request failed before cleanup: " + error); throw; }
            finally { try { stop.Cancel(); } finally { await running.WaitAsync(TimeSpan.FromSeconds(5)); } }
        }
    }
}
