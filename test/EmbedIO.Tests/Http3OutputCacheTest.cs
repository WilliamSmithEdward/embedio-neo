using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Caching;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task OutputCacheReplaysCompressedBytesAndInvalidatesOnHttp3(bool knownEmptyBody)
        {
            using var certificate = Certificate(); var prefix = Prefix();
            var cache = new OutputCache(); var policy = new OutputCachePolicy(TimeSpan.FromMinutes(1), tags: new[] { "data" }); var calls = 0;
            var body = new string('x', 2048);
            using var server = new WebServer(options => options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Get, context => cache.ExecuteAsync(context, policy, captured =>
                { Interlocked.Increment(ref calls); return ResponseSerializer.None(false)(captured, body); }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var running = server.RunAsync(stop.Token);
            using var client = Client(certificate); client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip");
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    using var request = Request(HttpMethod.Get, prefix + "value", knownEmptyBody ? new ByteArrayContent(Array.Empty<byte>()) : null);
                    using var response = await client.SendAsync(request, stop.Token);
                    Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30)); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
                    Assert.That(response.Content.Headers.GetValues("Content-Type").Count(), Is.EqualTo(1));
                    using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(stop.Token), CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip); Assert.That(await reader.ReadToEndAsync(stop.Token), Is.EqualTo(body));
                }
                Assert.That(calls, Is.EqualTo(knownEmptyBody ? 1 : 3)); Assert.That(cache.Count, Is.EqualTo(knownEmptyBody ? 1 : 0)); cache.InvalidateTag("data");
                using var refreshRequest = Request(HttpMethod.Get, prefix + "value", knownEmptyBody ? new ByteArrayContent(Array.Empty<byte>()) : null);
                using var refreshed = await client.SendAsync(refreshRequest, stop.Token); await refreshed.Content.ReadAsByteArrayAsync(stop.Token);
                Assert.That(refreshed.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(calls, Is.EqualTo(knownEmptyBody ? 2 : 4));
            }
            finally { stop.Cancel(); await running; }
        }
    }
}
