using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue505_BinaryResponses
    {
        // Transport fixture, not a claim that these marker bytes form a renderable JPEG.
        private static readonly byte[] Payload = { 255, 216, 0, 128, 254, 127, 1, 255, 217 };

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task ClearingHeadersDoesNotDisableTheDefaultJsonSerializer(HttpListenerMode mode, bool append)
        {
            var probe = new Probe();
            using var fixture = new Fixture(mode, false, probe, append);
            using var response = await fixture.Client.GetAsync(fixture.Url + "json/1");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(probe.Cleared, Is.True, "Clear removed the current header collection before serialization.");
            Assert.That(response.Headers.Contains("X-Before-Clear"), Is.False);
            Assert.That(string.Join(",", response.Content.Headers.GetValues("Content-Type")), Does.Contain("application/json"));
            var data = await response.Content.ReadAsByteArrayAsync();
            Assert.That(JsonSerializer.Deserialize<byte[]>(data), Is.EqualTo(Payload), "The default serializer emits JSON/base64, not image bytes.");
            Assert.That(data, Is.Not.EqualTo(Payload));
        }

        [TestCase(HttpListenerMode.EmbedIO, false, false)]
        [TestCase(HttpListenerMode.EmbedIO, false, true)]
        [TestCase(HttpListenerMode.EmbedIO, true, false)]
        [TestCase(HttpListenerMode.EmbedIO, true, true)]
        public async Task PassthroughPreservesExactBinaryBytesAndOneMediaType(HttpListenerMode mode, bool buffered, bool requestGzip)
        {
            using var fixture = new Fixture(mode, buffered);
            using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Url + "media/1");
            if (requestGzip) request.Headers.AcceptEncoding.ParseAdd("gzip");
            using var response = await fixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var wireBytes = await response.Content.ReadAsByteArrayAsync();
            byte[] decoded;
            if (requestGzip)
            {
                Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
                using var input = new MemoryStream(wireBytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                await gzip.CopyToAsync(output);
                decoded = output.ToArray();
            }
            else
            {
                Assert.That(response.Content.Headers.ContentEncoding, Is.Empty);
                decoded = wireBytes;
            }
            Assert.That(decoded, Is.EqualTo(Payload));
            Assert.That(response.Content.Headers.GetValues("Content-Type"), Is.EqualTo(new[] { "image/jpeg" }));
            Assert.That(response.Headers.GetValues("X-Media-Probe"), Is.EqualTo(new[] { "retained" }));
            if (buffered)
            {
                Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(wireBytes.Length));
                Assert.That(response.Headers.TransferEncodingChunked, Is.Not.True);
            }
            else Assert.That(response.Headers.TransferEncodingChunked, Is.True);
            using var json = await fixture.Client.GetAsync(fixture.Url + "api/status");
            Assert.That(((json).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).MediaType, Is.EqualTo("application/json"));
            Assert.That(await json.Content.ReadAsStringAsync(), Is.EqualTo("{\"status\":\"ok\"}"));
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task MissingRoutesAndFilesReturn404AndAValidImageStillWorks(HttpListenerMode mode, bool buffered)
        {
            using var fixture = new Fixture(mode, buffered);
            foreach (var (path, status) in new[] { ("media/", HttpStatusCode.NotFound), ("media/0", HttpStatusCode.NotFound),
                ("media/2", HttpStatusCode.NotFound) })
            {
                using var response = await fixture.Client.GetAsync(fixture.Url + path);
                Assert.That(response.StatusCode, Is.EqualTo(status), path);
            }
            Assert.That(await fixture.Client.GetByteArrayAsync(fixture.Url + "media/1"), Is.EqualTo(Payload));
        }

        private sealed class Probe { public bool Cleared; }

        private sealed class Fixture : IDisposable
        {
            private readonly CancellationTokenSource _stop = new();
            private readonly WebServer _server;
            private readonly Task _running;
            public string Url { get; } = Resources.GetServerAddress();
            public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(5) };
            public Fixture(HttpListenerMode mode, bool buffered, Probe? probe = null, bool append = false)
            {
                _server = new WebServer(o => o.WithUrlPrefix(Url).WithMode(mode))
                    .WithWebApi("/json", m => m.WithController(() => new MediaController(probe, append)))
                    .WithWebApi("/media", ResponseSerializer.None(buffered), m => m.WithController(() => new MediaController(null, false)))
                    .WithWebApi("/api", m => m.WithController<StatusController>());
                _running = _server.RunAsync(_stop.Token);
            }
            public void Dispose()
            {
                _stop.Cancel();
                _running.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                _server.Dispose();
                Client.Dispose();
                _stop.Dispose();
            }
        }

        private sealed class MediaController : WebApiController
        {
            private readonly Probe? _probe;
            private readonly bool _append;
            public MediaController(Probe? probe, bool append) { _probe = probe; _append = append; }
            [Route(HttpVerbs.Get, "/{roomId}")]
            public byte[] Get(ushort roomId)
            {
                if (roomId != 1) throw HttpException.NotFound();
                if (_probe != null)
                {
                    Response.Headers["X-Before-Clear"] = "removed";
                    Response.Headers.Clear();
                    _probe.Cleared = Response.Headers.Count == 0;
                    if (_append) Response.Headers.Add("Content-Type", "image/jpeg");
                    else Response.ContentType = "image/jpeg";
                }
                else
                {
                    Response.ContentType = "image/jpeg";
                    Response.ContentEncoding = null;
                    Response.Headers["X-Media-Probe"] = "retained";
                }
                return Payload;
            }
        }

        private sealed class StatusController : WebApiController
        {
            [Route(HttpVerbs.Get, "/status")]
            public object Get() => new { status = "ok" };
        }
    }
}
