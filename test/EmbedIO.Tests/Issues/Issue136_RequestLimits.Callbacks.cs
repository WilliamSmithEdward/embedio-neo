using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public partial class Issue136_RequestLimits
    {
        // The same no-read formatter probe passed accepted507 and failed the
        // private empty-refusal candidate; its assertions remain unchanged.
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task KnownBodyRefusalPreservesConfiguredErrorFormatterBeforeContinue(int protocol)
        {
            var callbacks = 0;
            var uploads = 0;
            const string marker = "configured-owner-refusal";
            await using var host = await MultiHost.StartAsync(protocol,
                options => options.WithMaximumRequestBodyBytes(2), async context =>
                {
                    if (context.Request.HttpMethod == "POST")
                    {
                        Interlocked.Increment(ref uploads);
                        Assert.Fail("Known oversized input must not reach the module.");
                    }
                    await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                }, HttpVerbs.Any, server => server.OnHttpException = async (context, error) =>
                {
                    Assert.That(error.StatusCode, Is.EqualTo(413));
                    Assert.That(error.Message, Is.EqualTo("Request body exceeds the configured byte limit."));
                    Interlocked.Increment(ref callbacks);
                    context.Response.Headers["X-Configured-Rejection"] = "routed-owner";
                    context.Response.ContentLength64 = WebServer.Utf8NoBomEncoding.GetByteCount(marker);
                    await context.SendStringAsync(marker, "text/plain", WebServer.Utf8NoBomEncoding);
                });
            if (protocol == 1)
            {
                var endpoint = new Uri(host.Url);
                using var tcp = new TcpClient();
                using var timeout = new CancellationTokenSource(Deadline);
                await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port, timeout.Token);
                using var peer = new RawSocket(tcp);
                // No entity bytes sent: the known length alone permits refusal.
                await peer.SendAsync($"POST /upload HTTP/1.1\r\nHost: {endpoint.Authority}\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n");
                var wire = await peer.ReadToEndAsync();
                Assert.That(wire, Does.StartWith("HTTP/1.1 413"));
                Assert.That(wire, Does.Not.Contain("HTTP/1.1 100"));
                Assert.That(wire, Does.Contain("X-Configured-Rejection: routed-owner").IgnoreCase);
                Assert.That(wire, Does.EndWith(marker));
            }
            else
            {
                using var content = new TrackingContent(5);
                using var response = await host.SendAsync(HttpMethod.Post, content,
                    request => request.Headers.ExpectContinue = true);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(marker));
                Assert.That(response.Headers.GetValues("X-Configured-Rejection"), Is.EqualTo(new[] { "routed-owner" }));
                Assert.That(content.Sent, Is.False);
            }
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(uploads, Is.Zero);
            using var healthy = await host.SendAsync(HttpMethod.Get, null);
            Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await healthy.Content.ReadAsStringAsync(), Is.EqualTo("healthy"));
            Assert.That(callbacks, Is.EqualTo(1));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task KnownBodyRefusalCallbackExplicitReadKeepsRawBudgetAndBorrowedTransport(int protocol)
        {
            var callbacks = 0;
            var uploads = 0;
            var bytesReturned = 0;
            var refusalPort = 0;
            var successorPort = 0;
            const string marker = "callback-read-refusal";
            await using var host = await MultiHost.StartAsync(protocol,
                options => options.WithMaximumRequestBodyBytes(2), async context =>
                {
                    if (context.Request.HttpMethod == "POST")
                    {
                        Interlocked.Increment(ref uploads);
                        Assert.Fail("The refused request must not reach the module.");
                    }
                    Volatile.Write(ref successorPort, context.Request.RemoteEndPoint.Port);
                    await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                }, HttpVerbs.Any, server => server.OnHttpException = async (context, error) =>
                {
                    Assert.That(error.StatusCode, Is.EqualTo(413));
                    Interlocked.Increment(ref callbacks);
                    Volatile.Write(ref refusalPort, context.Request.RemoteEndPoint.Port);
                    // Only the application's explicit read invites the upload.
                    // Reads still obey the captured raw entity budget.
                    var input = context.Request.InputStream;
                    var storage = new byte[] { 0x5a, 0x5a, 0x5a };
                    while (bytesReturned < 2)
                    {
                        var count = await input.ReadAsync(storage, bytesReturned, 2 - bytesReturned, context.CancellationToken);
                        Assert.That(count, Is.GreaterThan(0));
                        bytesReturned += count;
                    }
                    Assert.That(storage[0], Is.Zero);
                    Assert.That(storage[1], Is.Zero);
                    var failure = await Assert.CatchAsync<HttpException>(async () =>
                        _ = await input.ReadAsync(storage, 2, 1, context.CancellationToken));
                    Assert.That(failure, Is.SameAs(error));
                    Assert.That(storage[2], Is.EqualTo(0x5a), "The excess probe byte is never returned to the caller.");
                    input.Dispose();
                    // Application disposal cannot close the borrowed transport
                    // before the configured rejection response is written.
                    context.Response.ContentLength64 = WebServer.Utf8NoBomEncoding.GetByteCount(marker);
                    await context.SendStringAsync(marker, "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var content = new TrackingContent(5);
            using var response = await host.SendAsync(HttpMethod.Post, content, request => request.Headers.ExpectContinue = true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(marker));
            Assert.That(content.Sent, Is.True, "Explicit callback read requires the client to send the entity.");
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(bytesReturned, Is.EqualTo(2));
            Assert.That(uploads, Is.Zero);
            using var healthy = await host.SendAsync(HttpMethod.Get, null);
            Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await healthy.Content.ReadAsStringAsync(), Is.EqualTo("healthy"));
            Assert.That(callbacks, Is.EqualTo(1));
            if (protocol != 1)
                Assert.That(Volatile.Read(ref successorPort), Is.EqualTo(Volatile.Read(ref refusalPort)),
                    "The healthy multiplexed request must reuse the same actual peer connection.");
        }

    }
}
