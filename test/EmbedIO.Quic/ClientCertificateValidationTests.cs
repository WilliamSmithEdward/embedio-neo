using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Net;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    [SupportedOSPlatform("windows")]
    internal static class ClientCertificateValidationTests
    {
        internal static void Run()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=temporary validation root", key, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true)
            );
            using var root = rootRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-2),
                DateTimeOffset.UtcNow.AddDays(2)
            );
            using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            foreach (var kind in new[] { "valid", "expired", "future", "server-only" })
            {
                var request = new CertificateRequest("CN=validation " + kind, leafKey, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        [new Oid(kind == "server-only" ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")],
                        true
                    )
                );
                var now = DateTimeOffset.UtcNow;
                using var certificate = request.Create(
                    root,
                    kind == "expired" ? now.AddDays(-1) : kind == "future" ? now.AddHours(1) : now.AddMinutes(-1),
                    kind == "expired" ? now.AddHours(-1) : now.AddHours(2),
                    RandomNumberGenerator.GetBytes(16)
                );
                var calls = 0;
                var policy = new ServerTlsPolicy
                {
                    ClientCertificateMode = ClientCertificateMode.Require,
                    ClientCertificateValidator = (peer, chain, errors) =>
                    {
                        calls++;
                        Check(
                            peer.RawData
                                .AsSpan()
                                .SequenceEqual(certificate.RawData)
                                && chain != null
                                && errors == SslPolicyErrors.RemoteCertificateChainErrors,
                            "private root initially remains untrusted"
                        );

                        if (chain == null) return false;

                        Check(
                            chain.ChainPolicy.RevocationMode == X509RevocationMode.NoCheck
                                && chain.ChainPolicy.ApplicationPolicy.Count == 1
                                && chain.ChainPolicy.ApplicationPolicy[0].Value == "1.3.6.1.5.5.7.3.2",
                            "client chain policy defaults"
                        );
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(root);
                        return chain.Build(peer);
                    }
                };
                Check(
                    EmbedIOQuicTls.ValidateClientCertificate(policy, certificate, [root]) == (kind == "valid")
                        && calls == 1,
                    "date and client-EKU validation with a private test trust anchor"
                );
                Check(
                    !EmbedIOQuicTls.ValidateClientCertificate(
                        new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Require },
                        certificate,
                        [root]
                    ),
                    "default policy does not trust a supplied root"
                );
                var onlineCalls = 0;
                var online = new ServerTlsPolicy
                {
                    ClientCertificateMode = ClientCertificateMode.Require,
                    CheckCertificateRevocation = true,
                    ClientCertificateValidator = (_, chain, _) =>
                    {
                        onlineCalls++;
                        return chain?.ChainPolicy.RevocationMode == X509RevocationMode.Online;
                    }
                };
                Check(
                    EmbedIOQuicTls.ValidateClientCertificate(online, certificate, [root]) && onlineCalls == 1,
                    "configured revocation reaches the actual chain"
                );
                Check(
                    !EmbedIOQuicTls.ValidateClientCertificate(
                        new ServerTlsPolicy
                        {
                            ClientCertificateMode = ClientCertificateMode.Require,
                            ClientCertificateValidator = (_, _, _) =>
                                throw new InvalidOperationException("ordinary certificate validator")
                        },
                        certificate,
                        [root]
                    ),
                    "ordinary callback failures reject"
                );
                var critical = new OutOfMemoryException("critical certificate validator fixture");

                try
                {
                    EmbedIOQuicTls.ValidateClientCertificate(
                        new ServerTlsPolicy
                        {
                            ClientCertificateMode = ClientCertificateMode.Require,
                            ClientCertificateValidator = (_, _, _) => throw critical
                        },
                        certificate,
                        [root]
                    );
                    throw new InvalidOperationException("Critical validation error was hidden.");
                }

                catch (OutOfMemoryException error) when (ReferenceEquals(error, critical)) { }
            }

            foreach (var mode in new[] { ClientCertificateMode.Allow, ClientCertificateMode.Require })
            {
                var calls = 0;
                var policy = new ServerTlsPolicy
                {
                    ClientCertificateMode = mode,
                    ClientCertificateValidator = (_, _, _) =>
                    {
                        calls++;
                        return true;
                    }
                };
                Check(
                    EmbedIOQuicTls.ValidateClientCertificate(policy, null, []) == (mode == ClientCertificateMode.Allow)
                        && calls == 0,
                    "absent certificates follow the mode without invoking the certificate callback"
                );
            }
        }

        private static void Check(bool condition, string message) => QuicFuzzChecks.Require(condition, message);
    }
}
