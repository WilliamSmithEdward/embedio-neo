using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Files;
using EmbedIO.Health;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;
using NUnit.Framework;
using static EmbedIO.Tests.SecurityHeadersFixtures;

namespace EmbedIO.Tests
{
    public sealed partial class SecurityHeadersTest
    {
        [Test]
        public async Task NonCspFieldsDoNotBecomeNonceTemplates() =>
            await TestWebServer.UseAsync(
                s => s
                    .WithModule(
                        new SecurityHeadersModule(
                            "/",
                            new SecurityHeadersPolicy(permissionsPolicy: "unknown=({nonce})")
                        )
                    )
                    .WithModule(
                        new ActionModule(c => c.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding))
                    ),
                async client =>
                {
                    using var response = await client.GetAsync("/");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(Header(response, "Permissions-Policy"), Is.EqualTo("unknown=({nonce})"));
                        Assert.That(Header(response, "Cache-Control"), Is.Empty);
                    }
                }
            );

        [Test]
        public async Task ExistingCacheEntryCannotReplayIntoNonceResponse()
        {
            var cache = new OutputCache();
            var policy = new OutputCachePolicy(TimeSpan.FromMinutes(1));
            var nonceMode = false;
            var calls = 0;
            await TestWebServer.UseAsync(
                s => s.WithModule(new ActionModule(c =>
                    {
                        if (nonceMode) NoncePolicy().Apply(c);

                        return cache.ExecuteAsync(c, policy, async staged =>
                        {
                            calls++;
                            var bytes = WebServer.Utf8NoBomEncoding.GetBytes(nonceMode ? staged.GetSecurityHeadersNonce() : "old cached body");
                            staged.Response.ContentType = "text/plain";
                            staged.Response.ContentLength64 = bytes.Length;
                            await staged.Response.OutputStream.WriteAsync(bytes, staged.CancellationToken);
                        });
                    })),
                async client =>
                {
                    using var first = await client.GetAsync("/");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("old cached body"));
                        Assert.That(cache.Count, Is.EqualTo(1));
                    }

                    nonceMode = true;
                    using var second = await client.GetAsync("/");
                    var body = await second.Content.ReadAsStringAsync();

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(Header(second, "Content-Security-Policy"), Does.Contain("'nonce-" + body + "'"));
                        Assert.That(body, Is.Not.EqualTo("old cached body"));
                        Assert.That(calls, Is.EqualTo(2));
                    }
                }
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task StaticFilesRetainConditionalRangeAndHeadSemantics(bool nonce)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "embedio-security-"
                    + Guid
                        .NewGuid()
                        .ToString("N")
            );
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "file.txt"), "0123456789");

            try
            {
                await TestWebServer.UseAsync(
                    s => s
                        .WithModule(
                            new SecurityHeadersModule(
                                "/",
                                nonce ? NoncePolicy() : new SecurityHeadersPolicy("default-src 'none'")
                            )
                        )
                        .WithModule(new FileModule("/", new FileSystemProvider(directory, false))),
                    async client =>
                    {
                        using var first = await client.GetAsync("/file.txt");

                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                            Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("0123456789"));
                        }

                        var etag = Header(first, "ETag");
                        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/file.txt"));

                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(await head.Content.ReadAsStringAsync(), Is.Empty);
                            Assert.That(Header(head, "X-Content-Type-Options"), Is.EqualTo("nosniff"));
                        }

                        if (!nonce)
                        {
                            Assert.That(etag, Is.Not.Empty);
                            using var condition = new HttpRequestMessage(HttpMethod.Get, "/file.txt");
                            condition.Headers.TryAddWithoutValidation("If-None-Match", etag);
                            using var notModified = await client.SendAsync(condition);

                            using (Assert.EnterMultipleScope())
                            {
                                Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                                Assert.That(
                                    Header(notModified, "Content-Security-Policy"),
                                    Is.EqualTo("default-src 'none'")
                                );
                            }

                            using var range = new HttpRequestMessage(HttpMethod.Get, "/file.txt");
                            range.Headers.TryAddWithoutValidation("Range", "bytes=2-4");
                            using var partial = await client.SendAsync(range);

                            using (Assert.EnterMultipleScope())
                            {
                                Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                                Assert.That(await partial.Content.ReadAsStringAsync(), Is.EqualTo("234"));
                            }
                        }

                        else
                        {
                            using (Assert.EnterMultipleScope())
                            {
                                Assert.That(etag, Is.Empty);
                                Assert.That(Header(first, "Cache-Control"), Is.EqualTo("no-store"));
                            }
                        }
                    }
                );
            }

            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task WebSocketUpgradeRetainsSelectedPolicy(bool nonce)
        {
            var prefix = Resources.GetServerAddress();
            using var server = new WebServer(o =>
            {
                o.WithUrlPrefix(prefix);
                o.SecurityHeaders = nonce ? NoncePolicy() : new SecurityHeadersPolicy("default-src 'none'");
            }).WithModule(new SecurityEcho());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var ready = new HealthCheckService(server);
            var running = server.RunAsync(stop.Token);
            using var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;

            try
            {
                await ready.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new Uri("ws" + prefix[4..] + "socket"), stop.Token);
                var headers = socket.HttpResponseHeaders ?? throw new AssertionException("Expected handshake headers.");
                Assert.That(headers["X-Content-Type-Options"], Does.Contain("nosniff"));
                var csp = string.Join("", headers["Content-Security-Policy"]);
                Assert.That(csp, nonce ? Does.Contain("'nonce-") : Is.EqualTo("default-src 'none'"));

                if (nonce) Assert.That(headers["Cache-Control"], Does.Contain("no-store"));

                await socket.SendAsync(new ArraySegment<byte>([42]), WebSocketMessageType.Binary, true, stop.Token);
                var bytes = new byte[1];
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), stop.Token);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result.Count, Is.EqualTo(1));
                    Assert.That(bytes[0], Is.EqualTo(42));
                }

                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", stop.Token);
            }

            finally
            {
                socket.Abort();
                stop.Cancel();
                await running;
            }
        }

        private sealed class SecurityEcho : WebSocketModule
        {
            public SecurityEcho() : base("/socket", false) { }

            protected override Task OnMessageReceivedAsync(
                IWebSocketContext context,
                byte[] buffer,
                IWebSocketReceiveResult result
            ) => SendAsync(context, buffer);
        }
    }
}
