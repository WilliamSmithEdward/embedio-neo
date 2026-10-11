using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public class NativeHttp3WebServerTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task NativeHostingRunsNormalModulesAndCombinedTcpProtocols(bool combined)
        {
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") == null)
                Assert.Ignore("HTTP/3 is unavailable in the selected asset.");
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            var address = "https://localhost:" + new Uri(Resources.GetServerAddress()).Port + "/";
            var options = new WebServerOptions().WithUrlPrefix(address).WithCertificate(certificate)
                .WithMode(combined ? HttpListenerMode.EmbedIOCombined : HttpListenerMode.EmbedIOHttp3);
            using var server = (WebServer)(Activator.CreateInstance(typeof(WebServer), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { options, true }, null) ?? throw new AssertionException("Missing native host constructor."));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                context.SendStringAsync("native:" + context.Request.ProtocolVersion, "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, peer, _, _) => peer != null
                            && peer.GetCertHashString() == certificate.GetCertHashString(),
                    },
                };
                using var client = new HttpClient(handler);
                var versions = combined ? new[] { HttpVersion.Version11, HttpVersion.Version20, HttpVersion.Version30 }
                    : new[] { HttpVersion.Version30 };
                foreach (var version in versions)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, address)
                    { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                    using var response = await client.SendAsync(request, stop.Token);
                    Assert.That(response.Version, Is.EqualTo(version));
                    Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("native:" + version));
                }
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
