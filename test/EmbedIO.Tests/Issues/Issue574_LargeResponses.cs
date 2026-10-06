using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Files;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue574_LargeResponses
    {
        public static IEnumerable FileCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
                foreach (var cached in new[] { false, true })
                    foreach (var encoding in new[] { "identity", "gzip", "deflate" })
                        foreach (var megabytes in new[] { 1, 6 })
                            yield return new object[] { mode, cached, encoding, megabytes };
        }

        [TestCaseSource(nameof(FileCases))]
        public async Task LargeStaticResponseCompletesAndKeepsSmallRequestsAndRangesHealthy(HttpListenerMode mode, bool cached, string encoding, int megabytes)
        {
            var directory = Path.Combine(Path.GetTempPath(), "embedio-574-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var payload = MakePayload(megabytes * 1024 * 1024);
            await File.WriteAllBytesAsync(Path.Combine(directory, "test.js"), payload);
            await File.WriteAllTextAsync(Path.Combine(directory, "favicon.ico"), "small");
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithStaticFolder("/", directory, false, module =>
                {
                    module.ContentCaching = cached;
                    module.Cache = new FileCache { MaxFileSizeKb = 8192, MaxSizeKb = 32768 };
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                // Repeat on the same client to exercise the populated cache and keep-alive.
                for (var requestNumber = 0; requestNumber < 2; requestNumber++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url + "test.js");
                    request.Headers.AcceptEncoding.ParseAdd(encoding);
                    using var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    Assert.That(response.Content.Headers.ContentEncoding,
                        Is.EqualTo(encoding == "identity" ? Array.Empty<string>() : new[] { encoding }));
                    var wire = await response.Content.ReadAsByteArrayAsync();
                    Assert.That(Decode(wire, encoding), Is.EqualTo(payload));
                    Assert.That(await client.GetStringAsync(url + "favicon.ico"), Is.EqualTo("small"));
                }

                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url + "test.js"));
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(payload.Length));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url + "test.js");
                rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(payload.Length - 16385, payload.Length - 1);
                using var range = await client.SendAsync(rangeRequest);
                Assert.That(range.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                Assert.That(await range.Content.ReadAsByteArrayAsync(), Is.EqualTo(payload[^16385..]));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                server.Dispose();
                Directory.Delete(directory, true);
            }
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task LargeActionAndJsonResponsesCompleteWithoutDelayingOtherRequests(HttpListenerMode mode, bool buffered)
        {
            var url = Resources.GetServerAddress();
            var payload = MakePayload(6 * 1024 * 1024);
            var jsonValue = Encoding.ASCII.GetString(payload);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/api", ResponseSerializer.Json(buffered), m => m.WithController(() => new LargeController(jsonValue)))
                .WithModule(new ActionModule("/large", HttpVerbs.Get, async context =>
                {
                    using var output = context.OpenResponseStream(buffered, preferCompression: false);
                    await output.WriteAsync(payload, 0, payload.Length, context.CancellationToken);
                }))
                .WithModule(new ActionModule("/health", HttpVerbs.Get, context => context.SendStringAsync("healthy", MimeType.PlainText, WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                var bytes = client.GetByteArrayAsync(url + "large");
                var json = client.GetStringAsync(url + "api/value");
                Assert.That(await client.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
                Assert.That(await bytes, Is.EqualTo(payload));
                Assert.That(JsonSerializer.Deserialize<string>(await json), Is.EqualTo(jsonValue));
                Assert.That(await client.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [Test]
        public async Task UnreadResponseDoesNotBlockHealthRequestsAndCanBeCanceled()
        {
            var url = Resources.GetServerAddress();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var payload = MakePayload(1024 * 1024);
            using var writeStop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/large", HttpVerbs.Get, async context =>
                {
                    context.Response.ContentLength64 = 128L * payload.Length;
                    entered.TrySetResult();
                    try
                    {
                        for (var i = 0; i < 128; i++)
                            await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length, writeStop.Token);
                    }
                    catch (OperationCanceledException) when (writeStop.IsCancellationRequested) { }
                    catch (Exception error) { completed.TrySetException(error); }
                    finally { completed.TrySetResult(); }
                }))
                .WithModule(new ActionModule("/health", HttpVerbs.Get, context => context.SendStringAsync("healthy", MimeType.PlainText, WebServer.Utf8NoBomEncoding)));
            server.Listener.IgnoreWriteExceptions = false;
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var slowClient = new TcpClient { ReceiveBufferSize = 1024 };
            try
            {
                var uri = new Uri(url);
                await slowClient.ConnectAsync(uri.Host, uri.Port);
                await slowClient.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /large HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                Assert.That(await client.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
                Assert.That(completed.Task.IsCompleted, Is.False, "The deliberately unread large body should still be backpressured.");
                writeStop.Cancel();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await client.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
            }
            finally
            {
                slowClient.Dispose();
                writeStop.Cancel();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private static byte[] MakePayload(int length)
        {
            var payload = new byte[length];
            var random = new Random(574);
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + random.Next(26));
            return payload;
        }

        private static byte[] Decode(byte[] wire, string encoding)
        {
            if (encoding == "identity") return wire;
            using var input = new MemoryStream(wire);
            using Stream decoder = encoding == "gzip" ? new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            decoder.CopyTo(output);
            return output.ToArray();
        }

        public sealed class LargeController : WebApiController
        {
            private readonly string _value;
            public LargeController(string value) => _value = value;
            [Route(HttpVerbs.Get, "/value")]
            public string Get() => _value;
        }
    }
}
