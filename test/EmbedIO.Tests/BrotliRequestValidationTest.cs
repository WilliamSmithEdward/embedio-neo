using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class BrotliRequestValidationTest
    {
        private static byte[] AppendInvalidByte(byte[] source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var result = new byte[source.Length + 1];
            source.CopyTo(result, 0);
            result[^1] = 0xff;
            return result;
        }

        [TestCase("empty", false)]
        [TestCase("truncated", false)]
        [TestCase("trailing", false)]
        [TestCase("invalid", false)]
        [TestCase("empty", true)]
        [TestCase("truncated", true)]
        [TestCase("trailing", true)]
        [TestCase("invalid", true)]
        public async Task MalformedCodingReturnsBadRequestAndLeavesServerHealthy(string kind, bool text)
        {
            using var encoded = new MemoryStream();
            using (var writer = new BrotliStream(encoded, CompressionMode.Compress, true))
                writer.Write(Encoding.UTF8.GetBytes("Brotli request Zürich €"));
            var valid = encoded.ToArray();
            var body = kind switch
            {
                "empty" => Array.Empty<byte>(),
                "truncated" => valid[..^1],
                "trailing" => AppendInvalidByte(valid),
                "invalid" => new byte[] { 0xff, 0xff, 0xff, 0xff },
                _ => throw new AssertionException("Unknown malformed body fixture.")
            };
            var accepted = 0;
            var prefix = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(options =>
                options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIO).WithSupportCompressedRequests(true));
            server.OnAny(async context =>
            {
                if (context.Request.HttpMethod == "GET")
                {
                    await context.SendStringAsync("healthy", "text/plain", Encoding.UTF8);
                    return;
                }
                if (text) _ = await context.GetRequestBodyAsStringAsync();
                else _ = await context.GetRequestBodyAsByteArrayAsync();
                Interlocked.Increment(ref accepted);
                await context.SendStringAsync("accepted", "text/plain", Encoding.UTF8);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var content = new ByteArrayContent(body);
                content.Headers.ContentEncoding.Add("br");
                using var response = await client.PostAsync(prefix, content, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(accepted, Is.Zero);
                Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
