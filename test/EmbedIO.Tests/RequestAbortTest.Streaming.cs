using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Health;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class RequestAbortTest
    {
        [TestCase("sse")]
        [TestCase("json")]
        [TestCase("multipart")]
        [TestCase("multipart-chunked")]
        public async Task StreamingWorkCooperatesWithTheExplicitRequestToken(string kind)
        {
            var entered = Signal<bool>();
            var canceled = Signal<bool>();
            await WithServer(async context =>
            {
                var token = context.GetRequestAbortedToken(kind is "sse" or "json");
                try
                {
                    if (kind == "sse")
                    {
                        var events = context.OpenEventStream();
                        await events.WriteCommentAsync("ready", token);
                        entered.TrySetResult(true);
                        await Task.Delay(Timeout.Infinite, token);
                        await events.WriteAsync("unreachable", cancellationToken: token);
                    }
                    else if (kind == "json")
                        await JsonStream.Array(Items(token), JsonStreamingContext.Default.Int32).WriteAsync(context, token);
                    else
                    {
                        using var reader = context.OpenMultipartReader();
                        entered.TrySetResult(true);
                        await reader.ReadNextSectionAsync(token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(true); throw; }
                catch (IOException) when (kind.StartsWith("multipart", StringComparison.Ordinal))
                {
                    await Canceled(token);
                    canceled.TrySetResult(true);
                    throw;
                }
                async IAsyncEnumerable<int> Items([EnumeratorCancellation] CancellationToken itemToken)
                {
                    yield return 1;
                    entered.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, itemToken);
                }
            }, async (port, _) =>
            {
                var extra = kind.StartsWith("multipart", StringComparison.Ordinal) ? "Content-Type: multipart/form-data; boundary=boundary\r\n"
                    + (kind == "multipart" ? "Content-Length: 10000\r\n" : "Transfer-Encoding: chunked\r\n") : "";
                using var socket = await Request(port, extra: extra);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                socket.Shutdown(SocketShutdown.Send);
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });
        }

        [TestCase("fixed")]
        [TestCase("head")]
        [TestCase("chunked")]
        public async Task RequestAbortWireCompletionDetachesBeforeTheHandlerReturns(string framing)
        {
            var checkedToken = Signal<bool>();
            var release = Signal<bool>();
            CancellationToken captured = default;
            await WithServer(async context =>
            {
                using var parent = new CancellationTokenSource();
                context.GetImplementation().CancellationToken = parent.Token;
                captured = context.GetRequestAbortedToken();
                if (framing == "chunked")
                {
                    context.Response.SendChunked = true;
                    await context.SendStringAsync("abc", "text/plain", WebServer.Utf8NoBomEncoding);
                }
                else
                {
                    context.Response.ContentLength64 = 3;
                    if (framing == "head") await context.Response.OutputStream.FlushAsync();
                    else await context.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 });
                }
                parent.Cancel();
                var canceled = Signal<bool>();
                using var registration = captured.Register(() => canceled.TrySetResult(true));
                await Task.WhenAny(canceled.Task, Task.Delay(100));
                checkedToken.TrySetResult(!captured.IsCancellationRequested);
                await release.Task;
            }, async (port, _) =>
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    using var message = new HttpRequestMessage(framing == "head" ? HttpMethod.Head : HttpMethod.Get, $"http://127.0.0.1:{port}/complete");
                    using var response = await client.SendAsync(message);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(), Has.Length.EqualTo(framing == "head" ? 0 : 3));
                    Assert.That(await checkedToken.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.True, "The complete response must detach before handler return.");
                }
                finally { release.TrySetResult(true); }
            });
            Assert.That(captured.IsCancellationRequested, Is.False);
        }

        [Test]
        public async Task RequestDeadlineStillProducesItsExisting503Response()
        {
            CancellationToken captured = default;
            await WithServer(async context =>
            {
                captured = context.GetRequestAbortedToken();
                await Task.Delay(Timeout.Infinite, captured);
            }, async (port, _) =>
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/deadline");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                Assert.That(captured.IsCancellationRequested, Is.True);
            }, requestTimeout: TimeSpan.FromMilliseconds(100));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LateAcquisitionObservesAnAlreadyFailedBody(bool chunked)
        {
            var entered = Signal<bool>();
            var observed = Signal<bool>();
            await WithServer(async context =>
            {
                entered.TrySetResult(true);
                try
                {
                    var buffer = new byte[1];
                    await context.Request.InputStream.ReadExactlyAsync(buffer);
                    observed.TrySetResult(false);
                }
                catch (EndOfStreamException)
                {
                    var token = context.GetRequestAbortedToken();
                    await Canceled(token);
                    observed.TrySetResult(!context.CancellationToken.IsCancellationRequested);
                    context.Response.StatusCode = 400;
                }
            }, async (port, _) =>
            {
                using var socket = await Request(port, extra: chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: 10\r\n");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                socket.Shutdown(SocketShutdown.Send);
                Assert.That(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            });
        }

        [Test]
        public async Task TlsUploadObservationDoesNotConsumeCiphertextOrBodyBytes()
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var payload = new byte[256 * 1024]; new Random(1729).NextBytes(payload);
            CancellationToken captured = default;
            using var server = new WebServer(options => options.WithUrlPrefix($"https://127.0.0.1:{port}/").WithCertificate(certificate))
                .WithModule(new ActionModule(async context =>
                {
                    captured = context.GetRequestAbortedToken();
                    await Task.Delay(150, captured);
                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body, captured);
                    Assert.That(body.ToArray(), Is.EqualTo(payload));
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload, captured);
                }));
            using var health = new HealthCheckService(server);
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() },
            });
            try
            {
                await health.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                using var response = await client.PostAsync($"https://127.0.0.1:{port}/upload", new ByteArrayContent(payload));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(payload));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.That(captured.IsCancellationRequested, Is.False);
        }
    }
}
