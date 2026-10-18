using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Health;
using EmbedIO.Tus;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TusUploadTest
    {
        [TestCase(10)]
        [TestCase(11)]
        [TestCase(20)]
        public async Task TusResumesAcrossOwnedTcpConnections(int protocol)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            var prefix = "http://127.0.0.1:" + port + "/";
            using var server = new WebServer(prefix)
                .WithModule(Authentication())
                .WithModule(new TusUploadModule("/uploads/", store, (c, _) => c.User.Identity?.Name));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var ready = new HealthCheckService(server);
            var running = server.RunAsync(stop.Token);

            try
            {
                await ready.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    PooledConnectionLifetime = TimeSpan.Zero
                };
                using var client = new HttpClient(handler);
                var version = protocol == 10
                    ? HttpVersion.Version10
                    : protocol == 20 ? HttpVersion.Version20 : HttpVersion.Version11;
                await TusUploadWireSmoke.Sequence(client, prefix, version, stop.Token);
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    internal static class TusUploadWireSmoke
    {
        internal static async Task Sequence(HttpClient client, string prefix, Version version, CancellationToken token)
        {
            using var create = Request("POST", prefix + "uploads/");
            create.Headers.Add("Upload-Length", "6");
            using var creation = await Send(create);
            Assert.That(creation.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            var path = new Uri(new Uri(prefix), Header(creation, "Location")).ToString();
            using var one = Request("PATCH", path, "abc");
            one.Headers.Add("Upload-Offset", "0");
            using var partial = await Send(one);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(Header(partial, "Upload-Offset"), Is.EqualTo("3"));
            }

            using var foreign = Request("HEAD", path, owner: "bob");
            using var hidden = await Send(foreign);
            Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            using var head = Request("HEAD", path);
            using var progress = await Send(head);
            Assert.That(Header(progress, "Upload-Offset"), Is.EqualTo("3"));
            using var two = Request("PATCH", path, "def");
            two.Headers.Add("Upload-Offset", "3");
            using var complete = await Send(two);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(Header(complete, "Upload-Offset"), Is.EqualTo("6"));
            }

            async Task<HttpResponseMessage> Send(HttpRequestMessage request)
            {
                request.Version = version;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                var response = await client.SendAsync(request, token);
                Assert.That(response.Version, Is.EqualTo(version));
                return response;
            }
        }

        private static string Header(HttpResponseMessage response, string name) =>
            response.Headers
                .GetValues(name)
                .Single();

        private static HttpRequestMessage Request(
            string method,
            string path,
            string? body = null,
            string owner = "alice"
        )
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add("Authorization", "Bearer " + owner);
            request.Headers.Add("Tus-Resumable", "1.0.0");

            if (body != null)
            {
                request.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body));
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/offset+octet-stream");
            }

            return request;
        }
    }

    public partial class Http3ListenerTest
    {
        [Test]
        public async Task TusUploadsResumeOnOwnedHttp3Streams()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "embedio-tus-http3-"
                    + Guid
                        .NewGuid()
                        .ToString("N")
            );

            try
            {
                using var store = new DiskTusUploadStore(new TusUploadOptions(directory));
                using var certificate = Certificate();
                var prefix = Prefix();
                using var server = new WebServer(options => options
                    .WithUrlPrefix(prefix)
                    .WithMode(HttpListenerMode.EmbedIOHttp3)
                    .WithCertificate(certificate))
                    .WithModule(
                        new EmbedIO.Authentication.BearerAuthenticationModule(
                            "/",
                            (credential, _) => Task.FromResult<System.Security.Claims.ClaimsPrincipal?>(
                                new System.Security.Claims.ClaimsPrincipal(
                                    new System.Security.Claims.ClaimsIdentity(

                                        [
                                            new System.Security.Claims.Claim(
                                                System.Security.Claims.ClaimTypes.Name,
                                                credential
                                            )
                                        ],
                                        "fixture"
                                    )
                                )
                            )
                        )
                    )
                    .WithModule(new TusUploadModule("/uploads/", store, (c, _) => c.User.Identity?.Name));
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var running = server.RunAsync(stop.Token);
                using var client = Client(certificate);

                try
                {
                    await TusUploadWireSmoke.Sequence(client, prefix, HttpVersion.Version30, stop.Token);
                }

                finally
                {
                    stop.Cancel();
                    await running;
                }
            }

            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
