using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Files;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue567_ResponseCharset
    {
        [TestCase(null, "application/octet-stream", null)]
        [TestCase(null, "text/plain; charset=iso-8859-1", "iso-8859-1")]
        [TestCase("iso-8859-1", "text/plain", "iso-8859-1")]
        [TestCase("utf-8", "text/plain", "utf-8")]
        [TestCase("utf-8", "text/plain; charset=iso-8859-1", "iso-8859-1")]
        [TestCase("utf-8", "text/plain; CHARSET=iso-8859-1", "iso-8859-1")]
        [TestCase("utf-8", "text/plain; charset = \"iso-8859-1\"", "\"iso-8859-1\"")]
        [TestCase("utf-8", "text/plain; note=\"charset=iso-8859-1\"", "utf-8")]
        [TestCase("utf-8", "text/plain; x-charset=iso-8859-1", "utf-8")]
        public async Task ManagedHeaderHonorsEncodingAndExplicitCharset(string? encoding, string contentType, string? expectedCharset)
        {
            var url = Resources.GetServerAddress();
            var bytes = new byte[] { 0, 255, 10, 128 };
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
                {
                    context.Response.ContentType = contentType;
                    context.Response.ContentEncoding = encoding == null ? null : Encoding.GetEncoding(encoding);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var type = response.Content.Headers.ContentType;
                Assert.That((type ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo(expectedCharset));
                Assert.That(type.Parameters.Count, Is.EqualTo(MediaTypeHeaderValue.Parse(contentType).Parameters.Count +
                    (expectedCharset != null && MediaTypeHeaderValue.Parse(contentType).CharSet == null ? 1 : 0)));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes), "Header encoding must not transcode raw bytes.");
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task TextWriterBytesUseTheRequestedEncoding(HttpListenerMode mode, bool buffered)
        {
            var url = Resources.GetServerAddress();
            var encoding = Encoding.Latin1;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
                {
                    context.Response.ContentType = MimeType.PlainText;
                    using var writer = context.OpenResponseText(encoding, buffered);
                    await writer.WriteAsync("café");
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(encoding.GetBytes("café")));
                Assert.That(((response).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet,
                    Is.EqualTo(mode == HttpListenerMode.EmbedIO ? "iso-8859-1" : null));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        public static IEnumerable FileCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO })
                foreach (var cached in new[] { false, true })
                    foreach (var zip in new[] { false, true })
                        yield return new object[] { mode, cached, zip };
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task UnconfiguredStaticFileHeadersKeepTheirExistingDefaults(HttpListenerMode mode, bool cached)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-567-default-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "data.bin"), new byte[] { 0, 255 });
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithStaticFolder("/", directory, false, module => module.ContentCaching = cached);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.GetAsync(url + "data.bin");
                response.EnsureSuccessStatusCode();
                Assert.That(((response).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet,
                    Is.EqualTo(mode == HttpListenerMode.EmbedIO ? "utf-8" : null));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 0, 255 }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
        }

        [TestCaseSource(nameof(FileCases))]
        public async Task ExistingFileApisSelectCharsetWithoutChangingBytes(HttpListenerMode mode, bool cached, bool zip)
            => await CheckFileResponses(mode, cached, zip, false);

        [TestCaseSource(nameof(FileCases))]
        public async Task FileCallbackSelectsCharsetForActualResourceAndEverySuccessfulResponse(HttpListenerMode mode, bool cached, bool zip)
            => await CheckFileResponses(mode, cached, zip, true);

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task CallbackAlsoPreparesDirectoryListings(HttpListenerMode mode, bool cached)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-567-list-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "hello.txt"), "hello");
            var url = Resources.GetServerAddress();
            var count = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithStaticFolder("/", directory, false, module =>
                {
                    module.DefaultDocument = null;
                    module.ContentCaching = cached;
                    module.DirectoryLister = DirectoryLister.Html;
                    module.OnPrepareResponse = (context, info) =>
                    {
                        Assert.That(info.IsDirectory, Is.True);
                        Interlocked.Increment(ref count);
                        context.Response.ContentEncoding = null;
                        context.Response.ContentType += "; charset=utf-8";
                    };
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using var response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    Assert.That(((response).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo("utf-8"));
                    Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("hello.txt"));
                }
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                Assert.That(((head).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo("utf-8"));
                using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
                conditional.Headers.IfNoneMatch.Add((head.Headers.ETag ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")));
                using var notModified = await client.SendAsync(conditional);
                Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                Assert.That(((notModified).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo("utf-8"));
                Assert.That(count, Is.EqualTo(4));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task ErrorsSkipTheCallbackAndCallbackFailureDoesNotStopTheServer(HttpListenerMode mode)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-567-errors-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "ok.txt"), "hello");
            await File.WriteAllTextAsync(Path.Combine(directory, "throw.txt"), "must not be sent");
            var url = Resources.GetServerAddress();
            var count = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithStaticFolder("/", directory, false, module => module.OnPrepareResponse = (context, info) =>
                {
                    Interlocked.Increment(ref count);
                    if (info.Name == "throw.txt") throw new InvalidOperationException("callback failure");
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var missing = await client.GetAsync(url + "missing.txt");
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                using var post = await client.PostAsync(url + "ok.txt", new StringContent("ignored"));
                Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
                using var invalidRange = new HttpRequestMessage(HttpMethod.Get, url + "ok.txt");
                invalidRange.Headers.Range = new RangeHeaderValue(100, 101);
                using var range = await client.SendAsync(invalidRange);
                Assert.That(range.StatusCode, Is.EqualTo(HttpStatusCode.RequestedRangeNotSatisfiable));
                using var unacceptable = new HttpRequestMessage(HttpMethod.Get, url + "ok.txt");
                unacceptable.Headers.AcceptEncoding.ParseAdd("identity;q=0, *;q=0");
                using var negotiation = await client.SendAsync(unacceptable);
                Assert.That(negotiation.StatusCode, Is.EqualTo(HttpStatusCode.NotAcceptable));
                Assert.That(count, Is.Zero);
                using var error = await client.GetAsync(url + "throw.txt");
                Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(await error.Content.ReadAsStringAsync(), Does.Not.Contain("must not be sent"));
                Assert.That(await client.GetStringAsync(url + "ok.txt"), Is.EqualTo("hello"));
                Assert.That(count, Is.EqualTo(2));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
        }

        [TestCase(HttpListenerMode.EmbedIO, 0)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        public async Task ColdCacheRangeAfterHeadSendsOnlySelectedBytes(HttpListenerMode mode, int offset)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-567-cold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var bytes = new byte[] { 0, 255, 10, 128 };
            await File.WriteAllBytesAsync(Path.Combine(directory, "payload.bin"), bytes);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithStaticFolder("/", directory, false);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url + "payload.bin"));
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(4));
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url + "payload.bin");
                    request.Headers.Range = new RangeHeaderValue(offset, offset + 1);
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                    Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(2));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes[offset..(offset + 2)]));
                }
                Assert.That(await client.GetByteArrayAsync(url + "payload.bin"), Is.EqualTo(bytes));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose(); Directory.Delete(directory, true); }
        }

        private static async Task CheckFileResponses(HttpListenerMode mode, bool cached, bool zip, bool callback)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-567-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var text = new UnicodeEncoding(false, false).GetBytes("café 漢字");
            var binary = new byte[] { 0, 255, 10, 128 };
            await File.WriteAllBytesAsync(Path.Combine(directory, "index.txt"), text);
            await File.WriteAllBytesAsync(Path.Combine(directory, "image.bin"), binary);
            var archivePath = directory + ".zip";
            if (zip) ZipFile.CreateFromDirectory(directory, archivePath);
            IFileProvider provider = zip ? new ZipFileProvider(archivePath) : new FileSystemProvider(directory, false);
            FileModule module = callback ? new FileModule("/", provider) : new ExplicitCharsetFileModule(provider);
            module.ContentCaching = cached;
            module.DefaultDocument = "index.txt";
            module.AddCustomMimeType(".txt", "text/plain");
            module.AddCustomMimeType(".bin", "application/octet-stream");
            var callbackCount = 0;
            if (callback)
                module.OnPrepareResponse = (context, info) =>
                {
                    Interlocked.Increment(ref callbackCount);
                    Assert.That(info.IsFile, Is.True);
                    Assert.That(info.Name, Is.EqualTo(context.RequestedPath == "/" ? "index.txt" : "image.bin"));
                    Assert.That(context.Response.Headers[HttpHeaderNames.ETag], Is.Not.Null);
                    if (context.Response.StatusCode == (int)HttpStatusCode.PartialContent)
                        Assert.That(context.Response.Headers[HttpHeaderNames.ContentRange], Does.StartWith("bytes 0-1/"));
                    context.Response.ContentEncoding = null;
                    if (info.Name == "index.txt") context.Response.ContentType += "; charset=utf-16";
                    context.Response.Headers["X-Mapped-File"] = info.Name;
                };
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                foreach (var (path, expected, charset) in new[] { ("", text, callback ? "utf-16" : null), ("image.bin", binary, (string?)null) })
                {
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        using var get = await client.GetAsync(url + path);
                        get.EnsureSuccessStatusCode();
                        Assert.That(((get).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo(charset));
                        Assert.That(await get.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected));
                        if (callback) Assert.That(get.Headers.GetValues("X-Mapped-File"), Is.EqualTo(new[] { path.Length == 0 ? "index.txt" : "image.bin" }));
                    }
                    using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url + path));
                    Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(((head).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo(charset));
                    Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(expected.Length));
                    Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                    using var conditional = new HttpRequestMessage(HttpMethod.Get, url + path);
                    conditional.Headers.IfNoneMatch.Add((head.Headers.ETag ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")));
                    using var notModified = await client.SendAsync(conditional);
                    Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                    Assert.That(((notModified).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo(charset));
                    using var request = new HttpRequestMessage(HttpMethod.Get, url + path);
                    request.Headers.Range = new RangeHeaderValue(0, 1);
                    using var partial = await client.SendAsync(request);
                    Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                    Assert.That(((partial).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo(charset));
                    Assert.That(await partial.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected[..2]));
                    foreach (var compression in new[] { "gzip", "deflate" })
                    {
                        using var compressedRequest = new HttpRequestMessage(HttpMethod.Get, url + path);
                        compressedRequest.Headers.AcceptEncoding.ParseAdd(compression);
                        using var compressed = await client.SendAsync(compressedRequest);
                        compressed.EnsureSuccessStatusCode();
                        Assert.That(((compressed).Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CharSet, Is.EqualTo(charset));
                        Assert.That(compressed.Content.Headers.ContentEncoding, Is.EqualTo(new[] { compression }));
                        using var input = new MemoryStream(await compressed.Content.ReadAsByteArrayAsync());
                        using Stream decoder = compression == "gzip" ? new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                        using var output = new MemoryStream();
                        await decoder.CopyToAsync(output);
                        Assert.That(output.ToArray(), Is.EqualTo(expected));
                    }
                }
                Assert.That(callbackCount, Is.EqualTo(callback ? 14 : 0), "Response callbacks must not be cached along with file bytes.");
                Assert.Throws<InvalidOperationException>(() => module.OnPrepareResponse = null);
            }
            finally
            {
                stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); server.Dispose();
                Directory.Delete(directory, true);
                if (zip) File.Delete(archivePath);
            }
        }

        private sealed class ExplicitCharsetFileModule : FileModule
        {
            public ExplicitCharsetFileModule(IFileProvider provider) : base("/", provider) { }
            protected override Task OnRequestAsync(IHttpContext context)
            {
                context.Response.ContentEncoding = null;
                return base.OnRequestAsync(context);
            }
        }
    }
}
