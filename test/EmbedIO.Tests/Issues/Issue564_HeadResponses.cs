using System;
using System.Collections;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue564_HeadResponses
    {
        public static IEnumerable LengthCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO })
                foreach (var assignment in new[] { "property", "header", "header-then-property", "property-then-header" })
                    foreach (var async in new[] { false, true })
                        yield return new object[] { mode, assignment, async };
        }

        [TestCaseSource(nameof(LengthCases))]
        public async Task OriginalMetadataOnlyControllerPreservesExplicitLength(HttpListenerMode mode, string assignment, bool async)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/", m => m.WithController(() => new HeadController(assignment)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url + (async ? "async" : "hello/world")));
                response.EnsureSuccessStatusCode();
                Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(123));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, false, false)]
        [TestCase(HttpListenerMode.EmbedIO, true, false)]
        [TestCase(HttpListenerMode.EmbedIO, false, true)]
        [TestCase(HttpListenerMode.EmbedIO, true, true)]
        public async Task HeadStreamWritesDoNotAppearBeforeTheNextResponse(HttpListenerMode mode, bool async, bool chunked)
        {
            var url = Resources.GetServerAddress();
            var payload = Encoding.ASCII.GetBytes("HEAD_BODY_MUST_NOT_BE_SENT");
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/head", HttpVerbs.Head, async context =>
                {
                    context.Response.SendChunked = chunked;
                    if (!chunked) context.Response.ContentLength64 = payload.Length;
                    if (async) await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length, context.CancellationToken);
                    else context.Response.OutputStream.Write(payload, 0, payload.Length);
                }))
                .WithModule(new ActionModule("/next", HttpVerbs.Get, async context =>
                {
                    var bytes = Encoding.ASCII.GetBytes("NEXT");
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var socket = new TcpClient();
                var uri = new Uri(url);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                var input = socket.GetStream();
                var request = Encoding.ASCII.GetBytes($"HEAD /head HTTP/1.1\r\nHost: {uri.Authority}\r\n\r\n");
                await input.WriteAsync(request, timeout.Token);
                using var reader = new StreamReader(input, Encoding.ASCII, false, 1024, true);
                var headStatus = await reader.ReadLineAsync(timeout.Token);
                Assert.That(headStatus, Does.StartWith("HTTP/1.1 200"));
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
                await input.WriteAsync(Encoding.ASCII.GetBytes($"GET /next HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), timeout.Token);
                var nextStatus = await reader.ReadLineAsync(timeout.Token);
                Assert.That(nextStatus, Does.StartWith("HTTP/1.1 200"), "A HEAD body or chunk terminator corrupts the next response.");
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
                var next = new char[4];
                Assert.That(await reader.ReadBlockAsync(next.AsMemory(), timeout.Token), Is.EqualTo(4));
                Assert.That(new string(next), Is.EqualTo("NEXT"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        public sealed class HeadController : WebApiController
        {
            private readonly string _assignment;
            public HeadController(string assignment) => _assignment = assignment;
            private void SetLength()
            {
                if (_assignment == "property-then-header")
                {
                    Response.ContentLength64 = 12345;
                    Response.Headers.Set(HttpHeaderNames.ContentLength, "123");
                    Assert.That(Response.ContentLength64, Is.EqualTo(123));
                    return;
                }
                if (_assignment != "property") Response.Headers.Set(HttpHeaderNames.ContentLength, _assignment == "header" ? "123" : "12345");
                if (_assignment != "header") Response.ContentLength64 = 123;
            }
            [Route(HttpVerbs.Head, "/hello/world")]
            public void Original() => SetLength();
            [Route(HttpVerbs.Head, "/async")]
            public async Task Async() { await Task.Yield(); SetLength(); }
        }

        public static IEnumerable SerializationCases()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO })
                foreach (var buffered in new[] { false, true })
                    foreach (var encoding in new[] { "identity", "gzip" })
                        yield return new object[] { mode, buffered, encoding };
        }

        [TestCaseSource(nameof(SerializationCases))]
        public async Task SerializedHeadHasNoBodyAndPreservesGetMetadata(HttpListenerMode mode, bool buffered, string encoding)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/", ResponseSerializer.Json(buffered), module => module.WithController<SerializedController>());
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var socket = new TcpClient();
                var uri = new Uri(url);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                var stream = socket.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HEAD /value HTTP/1.1\r\nHost: {uri.Authority}\r\nAccept-Encoding: {encoding}\r\n\r\n"), timeout.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                Assert.That(await reader.ReadLineAsync(timeout.Token), Does.StartWith("HTTP/1.1 200"));
                long? headLength = null;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) headLength = long.Parse(line[15..].Trim());
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /health HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), timeout.Token);
                Assert.That(await reader.ReadLineAsync(timeout.Token), Does.StartWith("HTTP/1.1 200"), "Serialized HEAD bytes and chunk delimiters must not enter the next response.");
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var get = new HttpRequestMessage(HttpMethod.Get, url + "value");
                get.Headers.AcceptEncoding.ParseAdd(encoding);
                using var response = await client.SendAsync(get);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (buffered) Assert.That(headLength, Is.EqualTo(bytes.Length));
                else Assert.That(headLength, Is.Null, "An unknown streaming representation length may be omitted.");
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task HeadStreamPreservesCancellationValidationAndApmCompletion(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            var validated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/", HttpVerbs.Head, async context =>
                {
                    try
                    {
                        var stream = context.Response.OutputStream;
                        Assert.Throws<ArgumentNullException>(() => TestObjects.InvalidInput.Invoke((Action<byte[], int, int>)stream.Write, null, 0, 0));
                        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(new byte[1], -1, 1));
                        Assert.Throws<ArgumentException>(() => stream.Write(new byte[1], 0, 2));
                        Assert.Throws<NotSupportedException>(() => stream.SetLength(123));
                        using var canceled = new CancellationTokenSource(); canceled.Cancel();
                        await Assert.CatchAsync<OperationCanceledException>(async () => await stream.WriteAsync(new byte[1], 0, 1, canceled.Token));
                        await Assert.CatchAsync<OperationCanceledException>(async () => await stream.FlushAsync(canceled.Token));
                        var state = new object();
                        var callback = new TaskCompletionSource<IAsyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var result = stream.BeginWrite(new byte[] { 42 }, 0, 1, r => callback.TrySetResult(r), state);
                        stream.EndWrite(result);
                        Assert.That(await callback.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.SameAs(result));
                        Assert.That(result.AsyncState, Is.SameAs(state));
                        stream.WriteByte(42);
                        await stream.WriteAsync(new byte[] { 42 }.AsMemory(), context.CancellationToken);
                        stream.Flush(); await stream.FlushAsync(context.CancellationToken);
                        context.Response.ContentLength64 = 123;
                        stream.Dispose();
                        Assert.Throws<ObjectDisposedException>(() => stream.Write(new byte[1], 0, 1));
                        validated.TrySetResult(true);
                    }
                    catch (Exception exception) { validated.TrySetException(exception); throw; }
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                response.EnsureSuccessStatusCode();
                Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(123));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
                Assert.That(await validated.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        public sealed class SerializedController : WebApiController
        {
            [Route(HttpVerbs.Head, "/value")]
            [Route(HttpVerbs.Get, "/value")]
            public object Value() => new { text = "café 漢字" };
            [Route(HttpVerbs.Get, "/health")]
            public string Health() => "healthy";
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task HeadErrorResponseHasNoHtmlBodyAndServerRemainsHealthy(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/error", HttpVerbs.Head, _ => throw new InvalidOperationException("expected")))
                .WithModule(new ActionModule("/next", HttpVerbs.Get, context => context.SendStringAsync("healthy", MimeType.PlainText, Encoding.UTF8)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var socket = new TcpClient();
                var uri = new Uri(url);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                var stream = socket.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HEAD /error HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), timeout.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                Assert.That(await reader.ReadLineAsync(timeout.Token), Does.StartWith("HTTP/1.1 500"));
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
                var extra = new char[1];
                try { Assert.That(await reader.ReadAsync(extra.AsMemory(), timeout.Token), Is.Zero, "HEAD errors must not leak HTML or chunk delimiters."); }
                catch (IOException) { /* A reset is allowed by the existing error close policy. */ }
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                Assert.That(await client.GetStringAsync(url + "next"), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
}
