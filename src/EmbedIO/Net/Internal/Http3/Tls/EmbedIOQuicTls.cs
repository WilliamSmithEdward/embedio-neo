#if NET10_0_OR_GREATER
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Quic;

namespace EmbedIO.Net.Internal.Http3
{
    internal static class EmbedIOQuicTls
    {
        internal static Func<byte[], TlsSession> CreateFactory(
            ServerCertificateSource certificates,
            ServerTlsPolicy? policy
        )
        {
            ArgumentNullException.ThrowIfNull(certificates);
            policy?.EnsureSupported(true, false);

            policy?.Lock();

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) OpenSslTls.EnsureAvailable();

            Func<X509Certificate2?, X509Certificate2Collection, bool>? validate = policy?.RequestsClientCertificate
                == true
                ? (certificate, additional) => ValidateClientCertificate(policy, certificate, additional)
                : null;
            return parameters =>
            {
                // Capture the fallback per handshake, before a fragmented ClientHello
                // or a later publication can change this connection's selection.
                var fallback = certificates.Current;

                if (OperatingSystem.IsWindows())
                    return new SchannelTls(
                        name => SelectCertificate(policy, fallback, name),
                        parameters,
                        "h3",
                        false,
                        validate
                    );

                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    return new OpenSslTls(
                        name => SelectCertificate(policy, fallback, name),
                        parameters,
                        "h3",
                        validate
                    );

                throw new PlatformNotSupportedException("Recordless TLS requires a desktop platform.");
            };
        }

        internal static bool ValidateClientCertificate(
            ServerTlsPolicy policy,
            X509Certificate2? certificate,
            X509Certificate2Collection additional
        )
        {
            if (certificate == null)
                return policy.ValidateClientCertificate(
                    policy,
                    null,
                    null,
                    SslPolicyErrors.RemoteCertificateNotAvailable
                );

            using var chain = new X509Chain();
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            chain.ChainPolicy.RevocationMode = policy.CheckCertificateRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck;
            chain.ChainPolicy.ExtraStore.AddRange(additional);
            var errors = chain.Build(certificate) ? SslPolicyErrors.None : SslPolicyErrors.RemoteCertificateChainErrors;
            // The callback's certificate must not borrow the native query's short-lived lease.
            var callbackCertificate = policy.ClientCertificateValidator == null
                ? certificate
                : X509CertificateLoader.LoadCertificate(certificate.RawData);
            return policy.ValidateClientCertificate(policy, callbackCertificate, chain, errors);
        }

        private static X509Certificate2 SelectCertificate(
            ServerTlsPolicy? policy,
            X509Certificate2 fallback,
            string? name
        )
        {
            var selected = policy == null ? fallback : policy.SelectServerCertificate(name, fallback);
            return selected as X509Certificate2
                ?? throw new InvalidOperationException("TLS selection returned an unsupported certificate type.");
        }
    }
}
#endif
