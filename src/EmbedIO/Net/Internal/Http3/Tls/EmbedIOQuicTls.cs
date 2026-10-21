#if NET10_0_OR_GREATER
using System;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Quic;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    internal static class EmbedIOQuicTls
    {
        internal static Func<byte[], TlsSession> CreateFactory(
            ServerCertificateSource certificates,
            ServerTlsPolicy? policy
        )
        {
            ArgumentNullException.ThrowIfNull(certificates);
            policy?.EnsureSupported(true, false);

            if (policy?.RequestsClientCertificate == true)
                throw new NotSupportedException(
                    "The replacement TLS binding has not completed client-certificate integration."
                );

            policy?.Lock();
            return parameters =>
            {
                // Capture the fallback per handshake, before a fragmented ClientHello
                // or a later publication can change this connection's selection.
                var fallback = certificates.Current;
                return new SchannelTls(name => SelectCertificate(policy, fallback, name), parameters, "h3", false);
            };
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
