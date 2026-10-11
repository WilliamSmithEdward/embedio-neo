using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class BrotliResponseTest
    {
        private static bool SupportsBrotli => typeof(WebServer).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName.StartsWith(".NETCoreApp", StringComparison.Ordinal) == true;

        public static IEnumerable ResponseCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO })
                foreach (var buffered in new[] { false, true })
                    foreach (var text in new[] { false, true })
                        yield return new object[] { mode, buffered, text };
        }

        [TestCaseSource(nameof(ResponseCases))]
        public async Task ResponseHelpersSendDecodableBrotliAndPreserveSubsequentRequests(HttpListenerMode mode, bool buffered, bool text)
        {
            var payload = Encoding.UTF8.GetBytes(new string('a', 32768) + " Zürich €");
            var prefix = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(mode));
            server.OnAny(async context =>
            {
                context.Response.ContentType = MimeType.PlainText;
                if (text)
                {
                    using var writer = context.OpenResponseText(new UTF8Encoding(false), buffered);
                    await writer.WriteAsync(Encoding.UTF8.GetString(payload));
                }
                else
                {
                    using var output = context.OpenResponseStream(buffered);
                    await output.WriteAsync(payload);
                }
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                for (var i = 0; i < 3; i++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, prefix);
                    request.Headers.TryAddWithoutValidation("Accept-Encoding", i < 2 ? "br, identity;q=0" : "identity");
                    using var response = await client.SendAsync(request, stop.Token);
                    if (!SupportsBrotli && i < 2)
                    {
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotAcceptable));
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync(stop.Token);
                    Assert.That(response.Content.Headers.ContentEncoding, i < 2 ? Is.EqualTo(new[] { "br" }) : Is.Empty);
                    Assert.That(Decode(bytes, i < 2 ? CompressionMethod.Brotli : CompressionMethod.None), Is.EqualTo(payload));
                    Assert.That(response.Headers.Vary, Does.Contain("Accept-Encoding"));
                    if (buffered) Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(bytes.Length));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task StaticVariantsRemainDistinctAcrossColdWarmHeadAndConditionalResponses(HttpListenerMode mode, bool cached)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-br-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var payload = Encoding.UTF8.GetBytes(new string('b', 8192) + " €");
            await File.WriteAllBytesAsync(Path.Combine(directory, "test.txt"), payload);
            var prefix = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(mode))
                .WithStaticFolder("/", directory, false, module => module.ContentCaching = cached);
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string? brotliTag = null;
                foreach (var coding in new[] { "br", "gzip", "deflate", "identity", "br" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, prefix + "test.txt");
                    request.Headers.TryAddWithoutValidation("Accept-Encoding", coding + ", *;q=0");
                    using var response = await client.SendAsync(request, stop.Token);
                    if (!SupportsBrotli && coding == "br")
                    {
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotAcceptable));
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    var method = coding switch { "br" => CompressionMethod.Brotli, "gzip" => CompressionMethod.Gzip, "deflate" => CompressionMethod.Deflate, _ => CompressionMethod.None };
                    var bytes = await response.Content.ReadAsByteArrayAsync(stop.Token);
                    Assert.That(Decode(bytes, method), Is.EqualTo(payload));
                    Assert.That(response.Content.Headers.ContentEncoding, coding == "identity" ? Is.Empty : Is.EqualTo(new[] { coding }));
                    var tag = response.Headers.ETag?.ToString() ?? throw new AssertionException("Missing representation ETag.");
                    if (coding == "br")
                    {
                        Assert.That(tag, Does.EndWith("-br\""));
                        if (brotliTag != null) Assert.That(tag, Is.EqualTo(brotliTag));
                        brotliTag = tag;
                        using var headRequest = new HttpRequestMessage(HttpMethod.Head, prefix + "test.txt");
                        headRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "br, *;q=0");
                        using var head = await client.SendAsync(headRequest, stop.Token);
                        head.EnsureSuccessStatusCode();
                        Assert.That(head.Headers.ETag?.ToString(), Is.EqualTo(tag));
                        if (head.Content.Headers.TryGetValues("Content-Length", out var lengths))
                            Assert.That(lengths, Is.EqualTo(new[] { bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                        Assert.That(await head.Content.ReadAsByteArrayAsync(stop.Token), Is.Empty);
                        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, prefix + "test.txt");
                        conditionalRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "br, *;q=0");
                        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", tag);
                        using var conditional = await client.SendAsync(conditionalRequest, stop.Token);
                        Assert.That(conditional.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                        Assert.That(await conditional.Content.ReadAsByteArrayAsync(stop.Token), Is.Empty);
                    }
                    else if (brotliTag != null) Assert.That(tag, Is.Not.EqualTo(brotliTag));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); server.Dispose(); Directory.Delete(directory, true); }
        }

        [TestCase("br, identity;q=0", true, CompressionMethod.Brotli)]
        [TestCase("br;q=1, gzip;q=0.5, identity;q=0", true, CompressionMethod.Brotli)]
        [TestCase("gzip, br, deflate", true, CompressionMethod.Gzip)]
        [TestCase("deflate, br", true, CompressionMethod.Deflate)]
        [TestCase("br", false, CompressionMethod.None)]
        [TestCase("br;q=0, gzip", true, CompressionMethod.Gzip)]
        [TestCase("br, gzip", true, CompressionMethod.Brotli)]
        [TestCase("deflate, gzip, br", true, CompressionMethod.Deflate)]
        [TestCase("*", true, CompressionMethod.Gzip)]
        public void NegotiationRespectsWeightsExclusionsAndExistingTieOrder(string accept, bool prefer, CompressionMethod expected)
        {
            ArgumentNullException.ThrowIfNull(accept);
            var ok = new QValueList(true, accept).TryNegotiateContentEncoding(prefer, out var actual, out _);
            if (!SupportsBrotli && expected == CompressionMethod.Brotli)
            {
                Assert.That(ok, accept.Contains("gzip", StringComparison.Ordinal) ? Is.True : Is.False);
                if (ok) Assert.That(actual, Is.EqualTo(CompressionMethod.Gzip));
                return;
            }
            Assert.That(ok, Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }

        public static IEnumerable ConversionCases()
        {
            foreach (var source in new[] { CompressionMethod.None, CompressionMethod.Gzip, CompressionMethod.Deflate, CompressionMethod.Brotli })
                foreach (var target in new[] { CompressionMethod.None, CompressionMethod.Gzip, CompressionMethod.Deflate, CompressionMethod.Brotli })
                    if (source == CompressionMethod.Brotli || target == CompressionMethod.Brotli)
                        foreach (var length in new[] { 0, 200000 })
                            yield return new object[] { source, target, length };
        }

        [TestCaseSource(nameof(ConversionCases))]
        public void CacheConversionPreservesEmptyAndLargePayloads(CompressionMethod sourceMethod, CompressionMethod targetMethod, int length)
        {
            var payload = new byte[length];
            new Random(7932).NextBytes(payload);
            var source = Encode(payload, sourceMethod);
            var convert = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.CompressionUtility", true)?.GetMethod("ConvertCompression", BindingFlags.Public | BindingFlags.Static)
                ?? throw new AssertionException("Missing conversion entry point.");
            if (!SupportsBrotli)
            {
                var exception = Assert.Throws<TargetInvocationException>(() => convert.Invoke(null, new object[] { source, sourceMethod, targetMethod }));
                Assert.That(exception?.InnerException, Is.TypeOf<NotSupportedException>());
                return;
            }
            var result = convert.Invoke(null, new object[] { source, sourceMethod, targetMethod }) as byte[] ?? throw new AssertionException("Missing conversion output.");
            Assert.That(Decode(result, targetMethod), Is.EqualTo(payload));
            if (sourceMethod == targetMethod) Assert.That(result, Is.SameAs(source));
        }

        private static byte[] Encode(byte[] bytes, CompressionMethod method)
        {
            if (method == CompressionMethod.None) return bytes;
            using var output = new MemoryStream();
            using (Stream encoder = method switch
            {
                CompressionMethod.Brotli => new BrotliStream(output, CompressionMode.Compress, true),
                CompressionMethod.Gzip => new GZipStream(output, CompressionMode.Compress, true),
                _ => new DeflateStream(output, CompressionMode.Compress, true)
            }) encoder.Write(bytes);
            return output.ToArray();
        }

        private static byte[] Decode(byte[] bytes, CompressionMethod method)
        {
            if (method == CompressionMethod.None) return bytes;
            using var input = new MemoryStream(bytes);
            using Stream decoder = method switch
            {
                CompressionMethod.Brotli => new BrotliStream(input, CompressionMode.Decompress),
                CompressionMethod.Gzip => new GZipStream(input, CompressionMode.Decompress),
                _ => new DeflateStream(input, CompressionMode.Decompress)
            };
            using var output = new MemoryStream();
            decoder.CopyTo(output);
            return output.ToArray();
        }
    }
}
