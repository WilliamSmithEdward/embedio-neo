using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EmbedIO.Net;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    internal static class ServerChainTests
    {
        internal static async Task<int> RunAsync(string profile, string directory)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("This store fixture requires Linux.");

            if (Environment.GetEnvironmentVariable("EMBEDIO_QUIC_DISPOSABLE_CERTIFICATE_STORE") != "1")
                throw new InvalidOperationException(
                    "Use this fixture only in an explicitly disposable certificate-store profile."
                );

            if (profile is not ("complete" or "partial" or "two-intermediates" or "direct-root" or "self-signed"))
                throw new ArgumentException("Unknown chain fixture profile.", nameof(profile));

            var suffix = Guid
                .NewGuid()
                .ToString("N");
            using var rootKey = RSA.Create(2048);
            using var root = Request("root-" + suffix, rootKey, true).CreateSelfSigned(
                DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow.AddHours(1)
            );
            using var intermediateKey = RSA.Create(2048);
            using var intermediate = Issue(Request("issuer-" + suffix, intermediateKey, true), root, intermediateKey);
            using var secondKey = RSA.Create(2048);
            using var second = profile == "two-intermediates"
                ? Issue(Request("second-" + suffix, secondKey, true), intermediate, secondKey)
                : null;
            using var key = RSA.Create(2048);
            var request = Request("localhost", key, false);
            using var leaf = profile == "self-signed"
                ? request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10))
                : Issue(request, second ?? (profile == "direct-root" ? root : intermediate), key);
            List<X509Certificate2> expected = profile is "direct-root" or "self-signed"
                ? []
                : second == null ? [intermediate] : [second, intermediate];
            using var authorities = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
            authorities.Open(OpenFlags.ReadWrite);
            List<X509Certificate2> installed = [];

            void Install(X509Certificate2 certificate)
            {
                using var publicCopy = X509CertificateLoader.LoadCertificate(certificate.RawData);
                authorities.Add(publicCopy);
                installed.Add(certificate);
            }

            try
            {
                if (profile is not ("partial" or "self-signed")) Install(root);

                foreach (var certificate in expected) Install(certificate);

                return await RunWireAsync(
                    leaf,
                    profile == "self-signed" ? leaf : root,
                    expected,
                    directory
                ).ConfigureAwait(false);
            }

            finally
            {
                foreach (var certificate in installed) authorities.Remove(certificate);
            }
        }

        private static CertificateRequest Request(string name, RSA key, bool authority)
        {
            var request = new CertificateRequest(
                "CN=" + name,
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    authority ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature,
                    true
                )
            );

            if (!authority)
            {
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], true));
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                request.CertificateExtensions.Add(names.Build());
            }

            return request;
        }

        private static X509Certificate2 Issue(CertificateRequest request, X509Certificate2 issuer, RSA key)
        {
            using var certificate = request.Create(
                issuer,
                DateTimeOffset.UtcNow.AddMinutes(-10),
                issuer.NotAfter
                    .ToUniversalTime()
                    .AddMinutes(-1),
                RandomNumberGenerator.GetBytes(16)
            );
            return certificate.CopyWithPrivateKey(key);
        }

        [SupportedOSPlatform("linux")]
        private static async Task<int> RunWireAsync(
            X509Certificate2 leaf,
            X509Certificate2 trust,
            List<X509Certificate2> expected,
            string directory
        )
        {
            var context = SslStreamCertificateContext.Create(leaf, null, offline: true);
            QuicFuzzChecks.Require(
                context.IntermediateCertificates.Count == expected.Count,
                "BCL control chain matches configured profile"
            );
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "root.pem"), trust.ExportCertificatePem());
            var tls = EmbedIOQuicTls.CreateFactory(new ServerCertificateSource(leaf), null);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var tcpTask = Task.Run(async () =>
            {
                using var client = await tcp
                    .AcceptTcpClientAsync(deadline.Token)
                    .ConfigureAwait(false);
                using var stream = new SslStream(client.GetStream());
                await stream
                    .AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = context,
                            EnabledSslProtocols = SslProtocols.Tls13
                        },
                        deadline.Token
                    )
                    .ConfigureAwait(false);
                await stream
                    .WriteAsync("tcp baseline"u8.ToArray(), deadline.Token)
                    .ConfigureAwait(false);
            });
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var stopping = Task.Run(Console.ReadLine);
            var clock = Stopwatch.StartNew();
            QuicConnection? connection = null;
            EndPoint? remote = null;
            var buffer = new byte[65536];
            var echoes = 0;

            try
            {
                var ready = new
                {
                    tcp = (tcp.LocalEndpoint as IPEndPoint ?? throw new InvalidOperationException()).Port,
                    port = (socket.LocalEndPoint as IPEndPoint ?? throw new InvalidOperationException()).Port,
                    intermediates = context.IntermediateCertificates.Count,
                    issuers = expected
                        .Select(value => value.GetCertHashString(HashAlgorithmName.SHA256))
                        .ToArray()
                };
                Console.WriteLine(JsonSerializer.Serialize(ready));

                while (!stopping.IsCompleted && !deadline.IsCancellationRequested)
                {
                    List<byte[]> output = [];

                    if (socket.Poll(10000, SelectMode.SelectRead))
                    {
                        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                        var length = socket.ReceiveFrom(buffer, ref sender);
                        remote ??= sender;

                        if (!remote.Equals(sender)) continue;

                        connection ??= new QuicConnection(
                            buffer.AsSpan(0, length),
                            tls,
                            (operations, _) => new StreamEchoApplication(operations),
                            clock.Elapsed.TotalMilliseconds
                        );
                        output = connection.Receive(buffer.AsSpan(0, length), clock.Elapsed.TotalMilliseconds);
                    }

                    else if (connection != null) output = connection.Tick(clock.Elapsed.TotalMilliseconds);

                    if (remote != null)
                        foreach (var packet in output) socket.SendTo(packet, remote);
                }

                echoes = (connection?.Application as StreamEchoApplication)?.Echoes ?? 0;
            }

            finally
            {
                connection?.Dispose();
                tcp.Stop();
                deadline.Cancel();
                await tcpTask.ConfigureAwait(false);

                foreach (var certificate in context.IntermediateCertificates) certificate.Dispose();
            }

            var report = new
            {
                echoes,
                contexts = OpenSslTls.ActiveContexts,
                blocks = OpenSslTls.ActiveBlocks
            };
            Console.WriteLine(JsonSerializer.Serialize(report));
            return echoes == 1 && OpenSslTls.ActiveContexts == 0 && OpenSslTls.ActiveBlocks == 0 ? 0 : 1;
        }
    }
}
