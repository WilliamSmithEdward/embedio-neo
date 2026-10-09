using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestDecompressionLimitTest
    {
        [TestCase("gzip", false, -1)]
        [TestCase("gzip", false, 0)]
        [TestCase("gzip", false, 1)]
        [TestCase("gzip", true, -1)]
        [TestCase("gzip", true, 0)]
        [TestCase("gzip", true, 1)]
        [TestCase("deflate", false, -1)]
        [TestCase("deflate", false, 0)]
        [TestCase("deflate", false, 1)]
        [TestCase("deflate", true, -1)]
        [TestCase("deflate", true, 0)]
        [TestCase("deflate", true, 1)]
        [TestCase("br", false, -1)]
        [TestCase("br", false, 0)]
        [TestCase("br", false, 1)]
        [TestCase("br", true, -1)]
        [TestCase("br", true, 0)]
        [TestCase("br", true, 1)]
        public Task LimitsCountDecodedUtf8BytesAndPreserveHealthyRequests(string coding, bool text, int delta)
            => Exchange(coding, text, new string('é', 1024), 2048 + delta);

        [TestCase("gzip", false)]
        [TestCase("gzip", true)]
        [TestCase("deflate", false)]
        [TestCase("deflate", true)]
        [TestCase("br", false)]
        [TestCase("br", true)]
        public Task ZeroLimitAcceptsEmptyDecodedContent(string coding, bool text)
            => Exchange(coding, text, string.Empty, 0);

        [TestCase("gzip")]
        [TestCase("deflate")]
        [TestCase("br")]
        public Task HighlyCompressibleContentIsLimitedByDecodedSize(string coding)
            => Exchange(coding, false, new string('x', 131072), 64);

        private static async Task Exchange(string coding, bool text, string payload, long maximum)
        {
            var expected = Encoding.UTF8.GetBytes(payload);
            using var encoded = new MemoryStream();
            using (Stream compressor = coding switch
            {
                "gzip" => new GZipStream(encoded, CompressionMode.Compress, true),
                "deflate" => new DeflateStream(encoded, CompressionMode.Compress, true),
                "br" => new BrotliStream(encoded, CompressionMode.Compress, true),
                _ => throw new AssertionException("Unknown fixture coding.")
            }) compressor.Write(expected);
            if (coding == "deflate" && expected.Length == 0) encoded.Write(new byte[] { 3, 0 });
            var status = expected.LongLength > maximum ? (HttpStatusCode)413 : HttpStatusCode.OK;
            var target = typeof(WebServer).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
                ?? throw new AssertionException("Missing target metadata.");
            if (coding == "br" && target.StartsWith(".NETStandard,", StringComparison.Ordinal)) status = HttpStatusCode.BadRequest;
            var prefix = Resources.GetServerAddress();
            var accepted = 0;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(options =>
            {
                options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIO).WithSupportCompressedRequests(true);
                options.WithMaximumDecompressedRequestBodyBytes(maximum);
            });
            server.OnAny(async context =>
            {
                if (context.Request.HttpMethod == "GET") { await context.SendStringAsync("healthy", "text/plain", Encoding.UTF8); return; }
                var actual = text ? Encoding.UTF8.GetBytes(await context.GetRequestBodyAsStringAsync()) : await context.GetRequestBodyAsByteArrayAsync();
                Assert.That(actual, Is.EqualTo(expected));
                Interlocked.Increment(ref accepted);
                await context.SendStringAsync("accepted", "text/plain", Encoding.UTF8);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var content = new ByteArrayContent(encoded.ToArray());
                content.Headers.ContentEncoding.Add(coding);
                using var response = await client.PostAsync(prefix, content, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(status));
                Assert.That(accepted, Is.EqualTo(status == HttpStatusCode.OK ? 1 : 0));
                Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase("byte", 4)]
        [TestCase("byte", 5)]
        [TestCase("array", 4)]
        [TestCase("array", 5)]
        [TestCase("span", 4)]
        [TestCase("span", 5)]
        [TestCase("async", 4)]
        [TestCase("async", 5)]
        [TestCase("memory", 4)]
        [TestCase("memory", 5)]
        public async Task StreamReadVariantsEnforceBoundaryAndStickyFailure(string mode, int maximum)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.LimitedRequestStream")
                ?? throw new AssertionException("Missing limit stream.");
            using var source = new MemoryStream(new byte[] { 0, 127, 128, 255, 42 });
            using var reader = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { source, (long)maximum }, null) as Stream ?? throw new AssertionException("Missing limit reader.");
            var buffer = new byte[3];
            var total = 0;
            async Task ReadAll()
            {
                while (true)
                {
                    var count = mode switch
                    {
                        "byte" => reader.ReadByte() < 0 ? 0 : 1,
                        "array" => reader.Read(buffer, 0, buffer.Length),
                        "span" => reader.Read(buffer.AsSpan()),
                        "async" => await reader.ReadAsync(buffer, 0, buffer.Length),
                        "memory" => await reader.ReadAsync(buffer.AsMemory()),
                        _ => throw new AssertionException("Unknown read mode.")
                    };
                    if (count == 0) break;
                    total += count;
                }
            }
            if (maximum == 5)
            {
                await ReadAll();
                Assert.That(total, Is.EqualTo(5));
                Assert.That(reader.ReadByte(), Is.EqualTo(-1));
            }
            else
            {
                var error = await Assert.CatchAsync<HttpException>(ReadAll);
                Assert.That(error.StatusCode, Is.EqualTo(413));
                Assert.That(total, Is.LessThanOrEqualTo(maximum));
                var repeated = Assert.Throws<HttpException>(() => reader.ReadByte());
                Assert.That(repeated.StatusCode, Is.EqualTo(413));
            }
        }

        [Test]
        public void OptionPreservesDefaultAndEnforcesValidationAndLocking()
        {
            var options = new TestWebServerOptions();
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.Null);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.MaximumDecompressedRequestBodyBytes = -1);
            options.MaximumDecompressedRequestBodyBytes = long.MaxValue;
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.EqualTo(long.MaxValue));
            options.MaximumDecompressedRequestBodyBytes = null;
            options.Lock();
            Assert.Throws<InvalidOperationException>(() => options.MaximumDecompressedRequestBodyBytes = 0);
        }
    }
}
