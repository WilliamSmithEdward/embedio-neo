using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class QuicRuntimeRebindTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task DisposedRuntimeListenerRebindsSameEndpoint(bool connected)
        {
            var supported = QuicListener.IsSupported && QuicConnection.IsSupported;
            if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.That(supported, Is.True);
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) { Assert.Ignore("The host does not provide QUIC."); return; }
            await Rebind(connected);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Rebind(bool connected)
        {
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
            var expected = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var stage = "start";
            var cycle = 0;
            try
            {
                for (; cycle < 32; ++cycle)
                {
                    stage = "bind";
                    await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
                    {
                        ListenEndPoint = endpoint,
                        ApplicationProtocols = [SslApplicationProtocol.Http3],
                        ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                        {
                            DefaultCloseErrorCode = 0x100,
                            DefaultStreamErrorCode = 0x10c,
                            ServerAuthenticationOptions = new SslServerAuthenticationOptions
                            { ApplicationProtocols = [SslApplicationProtocol.Http3], ServerCertificate = certificate },
                        }),
                    }, deadline.Token);
                    endpoint = listener.LocalEndPoint;
                    if (!connected) continue;
                    stage = "connect";
                    var accepting = listener.AcceptConnectionAsync(deadline.Token).AsTask();
                    var owned = false;
                    try
                    {
                        await using var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
                        {
                            RemoteEndPoint = endpoint,
                            DefaultCloseErrorCode = 0x100,
                            DefaultStreamErrorCode = 0x10c,
                            ClientAuthenticationOptions = new SslClientAuthenticationOptions
                            {
                                TargetHost = "localhost",
                                ApplicationProtocols = [SslApplicationProtocol.Http3],
                                RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                                    && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                                    && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected,
                            },
                        }, deadline.Token);
                        await using var accepted = await accepting;
                        owned = true;
                        // Match listener drain: cease acceptance before closing existing peers.
                        stage = "dispose listener";
                        await listener.DisposeAsync();
                        stage = "close connection";
                        await accepted.CloseAsync(0x100, deadline.Token);
                        stage = "dispose connections";
                    }
                    finally
                    {
                        // Observe a failed accept even if the client handshake failed first.
                        await listener.DisposeAsync();
                        try { if (!owned) { await using var accepted = await accepting; } }
                        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or QuicException
                            or System.Security.Authentication.AuthenticationException)
                        { }
                    }
                }
            }
            finally
            {
                TestContext.Out.WriteLine($"Raw QUIC rebind: connected={connected}, cycle={cycle}, stage={stage}, endpoint={endpoint}, runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            }
        }
    }
}
