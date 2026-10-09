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
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestCodingChainTest
    {
        public static IEnumerable CodingCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
                foreach (var coding in new[] { "gzip, deflate", "deflate, gzip", "gzip, gzip", "br, gzip", "gzip, br", "gzip, br, deflate" })
                    foreach (var text in new[] { false, true })
                        foreach (var delta in new[] { -1, 0, 1 })
                            yield return new object[] { mode, coding, text, delta };
        }
        [TestCaseSource(nameof(CodingCases))]
        public Task DecodeInReverseOrderAndLimitFinalUtf8Bytes(HttpListenerMode mode, string coding, bool text, int delta)
        {
            ArgumentNullException.ThrowIfNull(coding);
            var body = Encoding.UTF8.GetBytes(new string('é', 1024) + " €");
            return Exchange(mode, coding, Encode(body, coding), body, true, body.Length + delta, text,
                LegacyRejects(coding) ? HttpStatusCode.BadRequest : delta < 0 ? (HttpStatusCode)413 : HttpStatusCode.OK);
        }

        public static IEnumerable ListCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
                foreach (var enabled in new[] { false, true })
                    foreach (var coding in new[] { "", "identity, identity", ",,", " , identity, " })
                        yield return new object[] { mode, enabled, coding };
        }
        [TestCaseSource(nameof(ListCases))]
        public Task EmptyAndIdentityListsNeedNoCodecAndKeepRawLimitScope(HttpListenerMode mode, bool enabled, string coding)
        {
            var body = Encoding.UTF8.GetBytes("uncompressed");
            return Exchange(mode, coding, body, body, enabled, 0, false, HttpStatusCode.OK);
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public Task EmptyDecodedChainWorksWithZeroLimit(HttpListenerMode mode, bool text)
            => Exchange(mode, "gzip, gzip", Encode(Array.Empty<byte>(), "gzip, gzip"), Array.Empty<byte>(), true, 0, text, HttpStatusCode.OK);

        [TestCase(HttpListenerMode.EmbedIO, 8)]
        [TestCase(HttpListenerMode.EmbedIO, 9)]
        [TestCase(HttpListenerMode.Microsoft, 8)]
        [TestCase(HttpListenerMode.Microsoft, 9)]
        public Task CodecDepthIsBoundedBeforeOpeningThePipeline(HttpListenerMode mode, int depth)
        {
            var coding = string.Join(",", System.Linq.Enumerable.Repeat("gzip", depth));
            var body = Encoding.UTF8.GetBytes("eight-layer boundary");
            return Exchange(mode, coding, Encode(body, coding), body, true, null, false,
                depth == 8 ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        }

        [TestCase(HttpListenerMode.EmbedIO, "gzip, unknown")]
        [TestCase(HttpListenerMode.Microsoft, "gzip, unknown")]
        [TestCase(HttpListenerMode.EmbedIO, "unknown, gzip")]
        [TestCase(HttpListenerMode.Microsoft, "unknown, gzip")]
        [TestCase(HttpListenerMode.EmbedIO, "gzip;level=1, gzip")]
        [TestCase(HttpListenerMode.Microsoft, "gzip;level=1, gzip")]
        [TestCase(HttpListenerMode.EmbedIO, "gzip, br;q=1")]
        [TestCase(HttpListenerMode.Microsoft, "gzip, br;q=1")]
        public Task UnsupportedAndParameterizedCodingsAreRejected(HttpListenerMode mode, string coding)
            => Exchange(mode, coding, new byte[] { 1, 2, 3 }, Array.Empty<byte>(), true, null, false, HttpStatusCode.BadRequest);

        [TestCase(HttpListenerMode.EmbedIO, "gzip, gzip")]
        [TestCase(HttpListenerMode.Microsoft, "gzip, gzip")]
        [TestCase(HttpListenerMode.EmbedIO, "gzip, br")]
        [TestCase(HttpListenerMode.Microsoft, "gzip, br")]
        public Task CompressedChainsStillRequireExplicitOptIn(HttpListenerMode mode, string coding)
        {
            ArgumentNullException.ThrowIfNull(coding);
            return Exchange(mode, coding, Encode(new byte[] { 1 }, coding), new byte[] { 1 }, false, null, false, HttpStatusCode.BadRequest);
        }

        public static IEnumerable MalformedCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
                foreach (var text in new[] { false, true })
                    foreach (var corruption in new[] { "outer-truncated", "outer-trailing", "inner-truncated", "inner-trailing" })
                        yield return new object[] { mode, text, corruption };
        }
        [TestCaseSource(nameof(MalformedCases))]
        public Task MalformedBrotliLayerCannotBecomeAnAcceptedBody(HttpListenerMode mode, bool text, string corruption)
        {
            ArgumentNullException.ThrowIfNull(corruption);
            var body = Encoding.UTF8.GetBytes("layer completion €");
            var outer = corruption.StartsWith("outer", StringComparison.Ordinal);
            var coding = outer ? "gzip, br" : "br, gzip";
            var encoded = outer ? Encode(body, coding) : Encode(body, "br");
            if (corruption.EndsWith("truncated", StringComparison.Ordinal)) encoded = encoded[..^1];
            else
            {
                var extra = new byte[encoded.Length + 1];
                encoded.CopyTo(extra, 0);
                extra[^1] = 0xff;
                encoded = extra;
            }
            if (!outer) encoded = Encode(encoded, "gzip");
            return Exchange(mode, coding, encoded, body, true, null, text, HttpStatusCode.BadRequest);
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task MixedCaseAndEmptyElementsWorkWithChunkedFraming(HttpListenerMode mode)
        {
            const string coding = " GZIP , , DeFlAtE, ";
            var body = Encoding.UTF8.GetBytes("chunked independent framing €");
            return Exchange(mode, coding, Encode(body, coding), body, true, body.Length, false, HttpStatusCode.OK, true);
        }

        [TestCase("byte", 0)]
        [TestCase("byte", 65536)]
        [TestCase("array", 0)]
        [TestCase("array", 65536)]
        [TestCase("span", 0)]
        [TestCase("span", 65536)]
        [TestCase("async", 0)]
        [TestCase("async", 65536)]
        [TestCase("memory", 0)]
        [TestCase("memory", 65536)]
        public async Task StreamVariantsPreserveBinaryContentAndReleaseOwnedSourceOnce(string mode, int length)
        {
            var bytes = new byte[length];
            new Random(9110).NextBytes(bytes);
            using var source = new CountingSource(Encode(bytes, "gzip, gzip"));
            var decoded = OpenChain(source);
            try
            {
                using var actual = new MemoryStream();
                var buffer = new byte[257];
                Assert.That(await decoded.ReadAsync(buffer, 0, 0), Is.Zero);
                while (true)
                {
                    if (mode == "byte")
                    {
                        var value = decoded.ReadByte();
                        if (value < 0) break;
                        actual.WriteByte((byte)value);
                        continue;
                    }
                    var count = mode switch
                    {
                        "array" => decoded.Read(buffer, 0, buffer.Length),
                        "span" => decoded.Read(buffer.AsSpan()),
                        "async" => await decoded.ReadAsync(buffer, 0, buffer.Length),
                        "memory" => await decoded.ReadAsync(buffer.AsMemory()),
                        _ => throw new AssertionException("Unknown stream mode.")
                    };
                    if (count == 0) break;
                    actual.Write(buffer, 0, count);
                }
                Assert.That(actual.ToArray(), Is.EqualTo(bytes));
                Assert.That(decoded.ReadByte(), Is.EqualTo(-1));
                Assert.That(source.Disposals, Is.Zero);
            }
            finally { decoded.Dispose(); decoded.Dispose(); }
            Assert.That(source.Disposals, Is.EqualTo(1));
        }

        [Test]
        public async Task PreCancelledReadPreservesTokenAndDoesNotConsumeTheSource()
        {
            using var source = new CountingSource(Encode(new byte[65536], "gzip, gzip"));
            using var decoded = OpenChain(source);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Exception? failure = null;
            try { _ = await decoded.ReadAsync(new byte[17], 0, 17, cancellation.Token); }
            catch (OperationCanceledException error) { failure = error; }
            var cancelled = failure as OperationCanceledException ?? throw new AssertionException("Missing cancellation.");
            Assert.That(cancelled.CancellationToken, Is.EqualTo(cancellation.Token));
            Assert.That(source.Position, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InvalidInnerCodingCannotBeRetriedIntoSuccessfulEof(bool async)
        {
            using var source = new CountingSource(Encode(new byte[] { 255, 255, 255, 255 }, "gzip"));
            using var decoded = OpenChain(source);
            var buffer = new byte[17];
            for (var retry = 0; retry < 2; retry++)
            {
                Exception? failure = null;
                try
                {
                    if (async) _ = await decoded.ReadAsync(buffer, 0, buffer.Length);
                    else _ = decoded.Read(buffer, 0, buffer.Length);
                }
                catch (HttpException error) { failure = error; }
                Assert.That(failure, Is.InstanceOf<HttpException>());
                Assert.That((failure as HttpException)?.StatusCode, Is.EqualTo(400));
            }
        }

        private static Stream OpenChain(Stream source)
        {
            var method = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.RequestCodingChain", true)?.GetMethod("TryOpen", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing chain factory.");
            var args = new object?[] { source, "gzip, gzip", true, null };
            Assert.That(method.Invoke(null, args), Is.EqualTo(true));
            return args[3] as Stream ?? throw new AssertionException("Missing decoded stream.");
        }
        private sealed class CountingSource(byte[] bytes) : MemoryStream(bytes)
        {
            internal int Disposals;
            protected override void Dispose(bool disposing)
            {
                if (disposing) Disposals++;
                base.Dispose(disposing);
            }
        }

        private static bool LegacyRejects(string coding)
            => coding.Contains("br", StringComparison.Ordinal) && typeof(WebServer).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName.StartsWith(".NETStandard", StringComparison.Ordinal) == true;

        private static async Task Exchange(HttpListenerMode mode, string coding, byte[] encoded, byte[] expected, bool enabled,
            long? limit, bool text, HttpStatusCode status, bool chunked = false)
        {
            var prefix = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(mode)
                .WithSupportCompressedRequests(enabled).WithMaximumDecompressedRequestBodyBytes(limit));
            var accepted = 0;
            server.OnAny(async context =>
            {
                if (context.Request.HttpMethod == "GET") { await context.SendStringAsync("healthy", "text/plain", Encoding.UTF8); return; }
                var bytes = text ? Encoding.UTF8.GetBytes(await context.GetRequestBodyAsStringAsync()) : await context.GetRequestBodyAsByteArrayAsync();
                Assert.That(bytes, Is.EqualTo(expected));
                Interlocked.Increment(ref accepted);
                await context.SendStringAsync("accepted", "text/plain", Encoding.UTF8);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var content = new ByteArrayContent(encoded);
                content.Headers.TryAddWithoutValidation("Content-Encoding", coding);
                using var request = new HttpRequestMessage(HttpMethod.Post, prefix) { Content = content };
                if (chunked) request.Headers.TransferEncodingChunked = true;
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(status));
                Assert.That(accepted, Is.EqualTo(status == HttpStatusCode.OK ? 1 : 0));
                Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        private static byte[] Encode(byte[] body, string coding)
        {
            foreach (var item in coding.Split(','))
            {
                var name = item.Trim().ToUpperInvariant();
                if (name.Length == 0 || name == "IDENTITY") continue;
                using var output = new MemoryStream();
                using (Stream encoder = name switch
                {
                    "GZIP" => new GZipStream(output, CompressionMode.Compress, true),
                    "DEFLATE" => new DeflateStream(output, CompressionMode.Compress, true),
                    "BR" => new BrotliStream(output, CompressionMode.Compress, true),
                    _ => throw new AssertionException("Unknown fixture coding.")
                }) encoder.Write(body);
                body = output.ToArray();
            }
            return body;
        }
    }
}
