using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #42 evaluates whether an exception handler can replace a response.
    // These cases pin the commitment contract: a response is replaceable until
    // its final headers reach the wire; afterwards a failure must end the
    // response in a way the client can detect, never as a complete-looking body.
    public class Issue42_DeferredResponses
    {
        private const string Partial = "partial body";
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        [TestCase(1)]
        [TestCase(2)]
        public async Task FailureBeforeHeadersIsReplacedByTheExceptionHandler(int protocol)
        {
            await using var host = await Host.StartAsync(context =>
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain";
                context.Response.Headers["X-Prepared"] = "yes";
                throw new InvalidOperationException("before headers");
            }, module => module.OnUnhandledException = ExceptionHandler.HtmlResponse);
            using var client = Host.CreateClient(protocol);
            using var response = await client.GetAsync(host.Url);
            Assert.That(response.Version.Major, Is.EqualTo(protocol));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
            Assert.That(response.Headers.Contains("X-Prepared"), Is.True, "Prepared headers survive; only the body and status are replaced.");
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("before headers"));
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task HttpExceptionBeforeHeadersReplacesTheConfiguredStatus(int protocol)
        {
            await using var host = await Host.StartAsync(context =>
            {
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = 1234;
                throw HttpException.Forbidden("not yours");
            });
            using var client = Host.CreateClient(protocol);
            using var response = await client.GetAsync(host.Url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("not yours"));
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task SerializationFailureHappensBeforeCommitment(int protocol)
        {
            await using var host = await Host.StartAsync(context => context.SendDataAsync(new ThrowingPayload()),
                module => module.OnUnhandledException = ExceptionHandler.HtmlResponse);
            using var client = Host.CreateClient(protocol);
            using var response = await client.GetAsync(host.Url);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("getter failed"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DeferredBufferNotDisposedOnFailureLetsTheHandlerReplaceTheResponse(bool compressed)
        {
            await using var host = await Host.StartAsync(async context =>
            {
                context.Response.ContentType = "text/plain";
                // Deferred pattern with the existing API: dispose only on success.
                var deferred = context.OpenResponseStream(buffered: true, preferCompression: compressed);
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await deferred.WriteAsync(bytes, 0, bytes.Length);
                throw new InvalidOperationException("after buffering");
            }, module => module.OnUnhandledException = ExceptionHandler.HtmlResponse);
            // Automatic decompression verifies that the replacement body matches
            // its Content-Encoding; a stale gzip label over HTML would fail here.
            using var client = Host.CreateClient(1, decompress: compressed);
            using var request = new HttpRequestMessage(HttpMethod.Get, host.Url);
            if (compressed) request.Headers.AcceptEncoding.ParseAdd("gzip");
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("after buffering").And.Not.Contain(Partial));
        }

        [Test]
        public async Task BufferedStreamDisposedWhileUnwindingCommitsThePartialBody()
        {
            await using var host = await Host.StartAsync(async context =>
            {
                context.Response.ContentType = "text/plain";
                using var buffered = context.OpenResponseStream(buffered: true, preferCompression: false);
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await buffered.WriteAsync(bytes, 0, bytes.Length);
                throw new InvalidOperationException("after buffering");
            });
            var wire = await host.ReadRawAsync("GET / HTTP/1.1");
            Assert.That(wire.StatusLine, Does.StartWith("HTTP/1.1 200"), "Disposal during unwinding already committed the buffer.");
            Assert.That(wire.Headers, Does.Contain("Content-Length: " + Partial.Length));
            Assert.That(wire.Body, Is.EqualTo(Partial), "Buffering alone is not rollback: the partial body is framed as complete.");
        }

        [Test]
        public async Task FailureAfterChunkedCommitmentDoesNotTerminateTheBody()
        {
            await using var host = await Host.StartAsync(async context =>
            {
                context.Response.ContentType = "text/plain";
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await context.Response.OutputStream.FlushAsync();
                throw new InvalidOperationException("after commitment");
            });
            var wire = await host.ReadRawAsync("GET / HTTP/1.1");
            Assert.That(wire.StatusLine, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire.Headers, Does.Contain("Transfer-Encoding: chunked"));
            Assert.That(wire.Body, Does.Contain(Partial));
            Assert.That(wire.Body, Does.Not.EndWith("0\r\n\r\n"), "A failed streaming response must not look complete.");
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task HttpExceptionAfterCommitmentDoesNotTerminateTheBody()
        {
            await using var host = await Host.StartAsync(async context =>
            {
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await context.Response.OutputStream.FlushAsync();
                throw HttpException.NotFound("too late");
            });
            var wire = await host.ReadRawAsync("GET / HTTP/1.1");
            Assert.That(wire.StatusLine, Does.StartWith("HTTP/1.1 200"), "A committed status cannot change.");
            Assert.That(wire.Body, Does.Contain(Partial).And.Not.Contain("too late"));
            Assert.That(wire.Body, Does.Not.EndWith("0\r\n\r\n"));
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task ModuleLevelHandlerCannotReplaceACommittedResponse()
        {
            var handlerCalls = 0;
            await using var host = await Host.StartAsync(async context =>
            {
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await context.Response.OutputStream.FlushAsync();
                throw new InvalidOperationException("after commitment");
            }, module => module.OnUnhandledException = (context, exception) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return context.SendStringAsync("replacement", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            var wire = await host.ReadRawAsync("GET / HTTP/1.1");
            Assert.That(wire.StatusLine, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire.Body, Does.Contain(Partial).And.Not.Contain("replacement"));
            Assert.That(wire.Body, Does.Not.EndWith("0\r\n\r\n"));
            Assert.That(handlerCalls, Is.Zero, "Handlers run only while the response is still replaceable.");
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task FailureAfterDeclaredLengthCommitmentClosesTheConnectionShort()
        {
            await using var host = await Host.StartAsync(async context =>
            {
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = Partial.Length * 2;
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await context.Response.OutputStream.FlushAsync();
                throw new InvalidOperationException("after commitment");
            });
            var wire = await host.ReadRawAsync("GET / HTTP/1.1");
            Assert.That(wire.StatusLine, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire.Headers, Does.Contain("Content-Length: " + Partial.Length * 2));
            Assert.That(wire.Body, Is.EqualTo(Partial), "The declared length is not satisfied, so the client detects truncation.");
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task FailureAfterCompressedCommitmentDoesNotTerminateTheBody()
        {
            await using var host = await Host.StartAsync(async context =>
            {
                context.Response.ContentType = "text/plain";
                var compressed = context.OpenResponseStream(buffered: false, preferCompression: true);
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await compressed.WriteAsync(bytes, 0, bytes.Length);
                await compressed.FlushAsync();
                throw new InvalidOperationException("after commitment");
            });
            var wire = await host.ReadRawAsync("GET / HTTP/1.1", "Accept-Encoding: gzip");
            Assert.That(wire.StatusLine, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire.Headers, Does.Contain("Content-Encoding: gzip").And.Contain("Transfer-Encoding: chunked"));
            Assert.That(wire.Body, Is.Not.Empty);
            Assert.That(wire.Body, Does.Not.EndWith("0\r\n\r\n"));
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task CancellationAfterCommitmentDoesNotTerminateTheBody()
        {
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(async context =>
            {
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await context.Response.OutputStream.FlushAsync();
                committed.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            });
            var reading = host.ReadRawAsync("GET / HTTP/1.1");
            await committed.Task.WaitAsync(Deadline);
            await host.StopAsync();
            var wire = await reading;
            Assert.That(wire.StatusLine, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire.Body, Does.Contain(Partial));
            Assert.That(wire.Body, Does.Not.EndWith("0\r\n\r\n"));
        }

        [Test]
        public async Task ClientDisconnectAfterCommitmentLeavesTheServerHealthy()
        {
            var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(async context =>
            {
                var bytes = new byte[65536];
                try
                {
                    using var timeout = new CancellationTokenSource(Deadline);
                    while (true)
                    {
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, timeout.Token);
                        await context.Response.OutputStream.FlushAsync(timeout.Token);
                    }
                }
                catch (Exception error)
                {
                    failed.TrySetResult(error);
                    throw;
                }
            }, ignoreWriteExceptions: false);
            using (var socket = await host.ConnectAsync())
            {
                await socket.WriteRequestAsync("GET / HTTP/1.1");
                var buffer = new byte[4096];
                Assert.That(await socket.ReadAsync(buffer, 0, buffer.Length).WaitAsync(Deadline), Is.GreaterThan(0));
            }
            var error = await failed.Task.WaitAsync(Deadline);
            Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<ObjectDisposedException>(), error.ToString());
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task FailureAfterCommitmentResetsTheHttp2Stream()
        {
            await using var host = await Host.StartAsync(async context =>
            {
                var bytes = Encoding.ASCII.GetBytes(Partial);
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await context.Response.OutputStream.FlushAsync();
                throw new InvalidOperationException("after commitment");
            });
            using var client = Host.CreateClient(2);
            using var response = await client.GetAsync(host.Url, HttpCompletionOption.ResponseHeadersRead);
            Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                async () => await response.Content.ReadAsStringAsync(),
                Throws.InstanceOf<IOException>().Or.InstanceOf<HttpRequestException>(),
                "The stream is reset instead of ending the partial body cleanly.");
            Assert.That(await client.GetStringAsync(host.Url + "probe"), Is.EqualTo("healthy"), "Other streams on the connection are unaffected.");
        }

        [Test]
        public async Task CompletedHelperResponseFollowedByAFailureIsDelivered()
        {
            await using var host = await Host.StartAsync(async context =>
            {
                await context.SendStringAsync("complete", "text/plain", WebServer.Utf8NoBomEncoding);
                throw new InvalidOperationException("after completion");
            });
            using var client = Host.CreateClient(1);
            Assert.That(await client.GetStringAsync(host.Url), Is.EqualTo("complete"), "A response the helper already completed is not aborted.");
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        private sealed class ThrowingPayload
        {
            public string Name => throw new InvalidOperationException("getter failed");
        }

        private sealed class RawResponse
        {
            internal RawResponse(string statusLine, string headers, string body, bool reset)
            {
                StatusLine = statusLine;
                Headers = headers;
                Body = body;
                Reset = reset;
            }

            internal string StatusLine { get; }
            internal string Headers { get; }
            internal string Body { get; }
            internal bool Reset { get; }
        }

        private sealed class RawSocket : IDisposable
        {
            private readonly TcpClient _client;
            private readonly string _authority;

            internal RawSocket(TcpClient client, string authority)
            {
                _client = client;
                _authority = authority;
            }

            internal NetworkStream Stream => _client.GetStream();

            internal Task WriteRequestAsync(string requestLine, params string[] headers)
            {
                var text = new StringBuilder(requestLine).Append("\r\nHost: ").Append(_authority).Append("\r\n");
                foreach (var header in headers) text.Append(header).Append("\r\n");
                text.Append("\r\n");
                var bytes = Encoding.ASCII.GetBytes(text.ToString());
                return Stream.WriteAsync(bytes, 0, bytes.Length);
            }

            internal Task<int> ReadAsync(byte[] buffer, int offset, int count) => Stream.ReadAsync(buffer, offset, count);

            public void Dispose() => _client.Dispose();
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop = new();
            private Task? _running;

            private Host(WebServer server, string url)
            {
                _server = server;
                Url = url;
            }

            internal string Url { get; }

            internal static Task<Host> StartAsync(Func<IHttpContext, Task> handler, Action<ActionModule>? configure = null, bool ignoreWriteExceptions = true)
            {
                var url = Resources.GetServerAddress();
                var module = new ActionModule("/", HttpVerbs.Get, context => context.Request.Url.AbsolutePath == "/probe"
                    ? context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)
                    : handler(context));
                configure?.Invoke(module);
                var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)).WithModule(module);
                server.Listener.IgnoreWriteExceptions = ignoreWriteExceptions;
                var host = new Host(server, url);
                host._running = server.RunAsync(host._stop.Token);
                return Task.FromResult(host);
            }

            internal static HttpClient CreateClient(int protocol, bool decompress = false)
            {
                var version = protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11;
                return new HttpClient(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = decompress ? DecompressionMethods.GZip : DecompressionMethods.None })
                {
                    DefaultRequestVersion = version,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Timeout = Deadline,
                };
            }

            internal async Task<string> ProbeAsync()
            {
                using var client = CreateClient(1);
                return await client.GetStringAsync(Url + "probe");
            }

            internal async Task<RawSocket> ConnectAsync()
            {
                var address = new Uri(Url);
                var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port);
                return new RawSocket(client, address.Authority);
            }

            // Reads one response until the server ends the connection. The request asks
            // for closure so a complete response also reaches EOF; a reset after the
            // server stopped sending counts as the end of the response.
            internal async Task<RawResponse> ReadRawAsync(string requestLine, params string[] headers)
            {
                using var socket = await ConnectAsync();
                var requestHeaders = new string[headers.Length + 1];
                headers.CopyTo(requestHeaders, 0);
                requestHeaders[headers.Length] = "Connection: close";
                await socket.WriteRequestAsync(requestLine, requestHeaders);
                var received = new MemoryStream();
                var buffer = new byte[16384];
                var reset = false;
                using var timeout = new CancellationTokenSource(Deadline);
                try
                {
                    while (true)
                    {
                        var count = await socket.Stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token);
                        if (count == 0) break;
                        received.Write(buffer, 0, count);
                    }
                }
                catch (IOException)
                {
                    reset = true;
                }
                var text = Encoding.Latin1.GetString(received.ToArray());
                var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                Assert.That(headEnd, Is.GreaterThan(0), "No complete response head was received: " + text);
                var head = text.Substring(0, headEnd);
                var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
                var statusLine = lineEnd < 0 ? head : head.Substring(0, lineEnd);
                var headerText = lineEnd < 0 ? string.Empty : head.Substring(lineEnd + 2);
                return new RawResponse(statusLine, headerText, text.Substring(headEnd + 4), reset);
            }

            internal async Task StopAsync()
            {
                _stop.Cancel();
                if (_running != null) await _running.WaitAsync(Deadline);
            }

            public async ValueTask DisposeAsync()
            {
                await StopAsync();
                _server.Dispose();
                _stop.Dispose();
            }
        }
    }
}
