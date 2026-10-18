using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Health;
using EmbedIO.Security;
using EmbedIO.Tus;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TusUploadTest
    {
        [TestCase("POST")]
        [TestCase("PATCH")]
        [TestCase("DELETE")]
        public async Task UpgradeHeadersCannotExemptUnsafeOverridesFromBrowserTokens(string effective)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            var prefix = "http://127.0.0.1:" + port + "/";
            using var protection = new BrowserRequestProtection(new BrowserRequestProtectionOptions([prefix]));
            using var server = new WebServer(prefix);
            var observedUpgrade = false;
            ConfigureBrowserUploads(server, store, protection, c => observedUpgrade = c.Request.IsWebSocketRequest);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var ready = new HealthCheckService(server);
            var running = server.RunAsync(stop.Token);

            try
            {
                await ready.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                using var handler = new SocketsHttpHandler { UseProxy = false };
                using var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(prefix),
                    Timeout = TimeSpan.FromSeconds(5)
                };
                var token = await UploadBrowserToken(client);
                var path = await CreateProtectedUpload(client, token);
                using var deniedRequest = OverrideRequest("GET", effective, path, null);
                UpgradeHeaders(deniedRequest, prefix);
                using var denied = await client.SendAsync(deniedRequest, stop.Token);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                    Assert.That(store.Count, Is.EqualTo(1));
                    Assert.That(
                        store
                            .GetUploads("alice")
                            .Single().Offset,
                        Is.Zero
                    );
                }

                using var allowedRequest = OverrideRequest("GET", effective, path, token);
                UpgradeHeaders(allowedRequest, prefix);
                using var allowed = await client.SendAsync(allowedRequest, stop.Token);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(
                        allowed.StatusCode,
                        Is.EqualTo(effective == "POST" ? HttpStatusCode.Created : HttpStatusCode.NoContent)
                    );
                    Assert.That(
                        observedUpgrade,
                        Is.True,
                        "Exercise the actual transport's WebSocket-request classification."
                    );
                }
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static void UpgradeHeaders(HttpRequestMessage request, string origin)
        {
            request.Headers.Connection.Add("Upgrade");
            request.Headers.Upgrade.ParseAdd("websocket");
            request.Headers.Add("Sec-WebSocket-Version", "13");
            request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
            request.Headers.Add("Origin", origin.TrimEnd('/'));
        }
    }
}
