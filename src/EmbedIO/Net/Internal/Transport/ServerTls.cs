using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    // TCP server handshakes under a listener's certificate and optional ServerTlsPolicy.
    internal static class ServerTls
    {
        private static readonly SslProtocols DefaultProtocols = SelectDefaultProtocols();

        private static SslProtocols SelectDefaultProtocols()
        {
#if NET10_0_OR_GREATER
            return SslProtocols.Tls12 | SslProtocols.Tls13;
#else
            // Resolve the executing runtime's public flag without requiring an
            // older runtime to accept a protocol its reference assembly lacks.
            return Enum.TryParse<SslProtocols>("Tls13", out var tls13) && Enum.IsDefined(typeof(SslProtocols), tls13)
                ? SslProtocols.Tls12 | tls13
                : SslProtocols.Tls12;
#endif
        }

        internal static SslProtocols GetProtocols(ServerTlsPolicy? policy) =>
            policy == null || policy.EnabledProtocols == SslProtocols.None ? DefaultProtocols : policy.EnabledProtocols;

        internal static SslStream CreateStream(Stream inner, ServerTlsPolicy? policy)
        {
#if NET10_0_OR_GREATER
            // The .NET 10 asset passes the validation callback with the authentication options.
            _ = policy;
            return new SslStream(inner, false);
#else
            return policy != null && policy.RequestsClientCertificate
                ? new SslStream(inner, false, policy.ValidateClientCertificate)
                : new SslStream(inner, false);
#endif
        }

        internal static Task AuthenticateAsync(SslStream tls, X509Certificate certificate, ServerTlsPolicy? policy)
        {
#if NET10_0_OR_GREATER
            var options = new SslServerAuthenticationOptions
            {
                EnabledSslProtocols = GetProtocols(policy),
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
            };

            // The runtime refuses both a certificate and a selection callback.
            if (policy?.ServerCertificateSelector != null)
                options.ServerCertificateSelectionCallback = (_, hostName) => policy.SelectServerCertificate(
                    hostName,
                    certificate
                );

            else
                options.ServerCertificate = certificate;

            if (policy != null && policy.RequestsClientCertificate)
            {
                options.ClientCertificateRequired = true;
                options.RemoteCertificateValidationCallback = policy.ValidateClientCertificate;
                options.CertificateRevocationCheckMode = policy.CheckCertificateRevocation
                    ? X509RevocationMode.Online
                    : X509RevocationMode.NoCheck;
            }

            return tls.AuthenticateAsServerAsync(options);
#else
            return tls.AuthenticateAsServerAsync(
                certificate,
                policy != null && policy.RequestsClientCertificate,
                GetProtocols(policy),
                policy != null && policy.CheckCertificateRevocation
            );
#endif
        }

        // A copy owned by the connection: the TLS stream may dispose its own instance when it closes.
        internal static X509Certificate2? CopyRemoteCertificate(X509Certificate? remote)
        {
            if (remote == null)
                return null;
#if NET10_0_OR_GREATER
            return X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
#else
            return new X509Certificate2(remote.GetRawCertData());
#endif
        }
    }
}
