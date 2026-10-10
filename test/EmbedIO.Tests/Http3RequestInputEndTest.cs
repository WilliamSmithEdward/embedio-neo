using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // RFC 9204 section 4.4.2: a decoder emits Stream Cancellation when a request
    // stream is reset or its reading is abandoned, not after reading it to FIN.
    public sealed class Http3RequestInputEndTest
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        public async Task OnlyAbandonedRequestInputIsCanceledOnTheDecoderStream()
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The netstandard asset exposes framing but no direct QUIC transport."); return; }
            await Run();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Run()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
            await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundBidirectionalStreams = 32,
                    MaxInboundUnidirectionalStreams = 8,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, ServerCertificate = cert }
                })
            }, deadline.Token);
            var assembly = typeof(WebServer).Assembly;
            var exchange = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange", true) ?? throw new AssertionException("Missing exchange.");
            var connection = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection", true) ?? throw new AssertionException("Missing connection.");
            var handler = (typeof(Http3RequestInputEndTest).GetMethod(nameof(RespondWithoutReading), Flags) ?? throw new AssertionException("Missing handler."))
                .MakeGenericMethod(exchange).CreateDelegate(typeof(Func<,>).MakeGenericType(exchange, typeof(Task)));
            var server = Task.Run(async () =>
            {
                var accepted = await listener.AcceptConnectionAsync(deadline.Token);
                await ((Task)(connection.GetMethod("RunAsync", Flags)?.Invoke(null, new object[] { accepted, handler, deadline.Token })
                    ?? throw new AssertionException("Missing runner.")));
            });
            await using var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == cert.GetCertHashString()
                }
            }, deadline.Token);
            // The control stream outlives the server: closing a critical stream
            // while the connection runs is a connection error.
            QuicStream? control = null;
            var serverStreams = new List<QuicStream>();
            try
            {
                control = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token);
                await control.WriteAsync(new byte[] { 0, 4, 0 }, deadline.Token);

                // GET: independently encoded static-table HEADERS, FIN in the same write.
                await using (var complete = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token))
                {
                    Assert.That(complete.Id, Is.Zero);
                    await complete.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, deadline.Token);
                    Assert.That(await ReadAll(complete, deadline.Token), Is.Not.Empty);
                }

                // POST with DATA the handler never reads and no FIN: input is abandoned.
                await using var abandoned = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                Assert.That(abandoned.Id, Is.EqualTo(4));
                await abandoned.WriteAsync(Convert.FromHexString("01100000D4D7C150096C6F63616C686F7374" + "0003616263"), false, deadline.Token);
                Assert.That(await ReadAll(abandoned, deadline.Token), Is.Not.Empty);

                var decoder = await AcceptServerStream(client, 3, serverStreams, deadline.Token);
                var instruction = new byte[1];
                Assert.That(await decoder.ReadAsync(instruction, deadline.Token), Is.EqualTo(1));
                // Stream Cancellation is 01xxxxxx with a six-bit stream ID prefix. A
                // cancellation for stream 0 would precede the one for stream 4.
                Assert.That(instruction[0], Is.EqualTo(0x40 | 4), "Only the abandoned request is canceled.");
            }
            finally
            {
                deadline.Cancel();
                try { await server; }
                catch (OperationCanceledException) { }
                catch (QuicException) { }
                if (control != null) await control.DisposeAsync();
                foreach (var stream in serverStreams) await stream.DisposeAsync();
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<QuicStream> AcceptServerStream(QuicConnection client, byte type, List<QuicStream> accepted, CancellationToken token)
        {
            while (true)
            {
                var stream = await client.AcceptInboundStreamAsync(token);
                // Server critical streams stay open: abandoning one is a connection error.
                accepted.Add(stream);
                var first = new byte[1];
                if (await stream.ReadAsync(first, token) == 1 && first[0] == type) return stream;
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<byte[]> ReadAll(QuicStream stream, CancellationToken token)
        {
            using var response = new MemoryStream();
            await stream.CopyToAsync(response, token);
            return response.ToArray();
        }

        private static async Task RespondWithoutReading<T>(T value)
        {
            object target = value ?? throw new AssertionException("Missing exchange.");
            var type = target.GetType();
            var token = (CancellationToken)(type.GetProperty("CancellationToken", Flags)?.GetValue(target) ?? throw new AssertionException("Missing token."));
            await ((Task)(type.GetMethod("RespondAsync", Flags)?.Invoke(target, new object[] { new byte[] { (byte)'o', (byte)'k' }, token })
                ?? throw new AssertionException("Missing response.")));
        }
    }
}
