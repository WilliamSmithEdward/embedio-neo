#if NET10_0_OR_GREATER
using System;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Quic;

namespace EmbedIO.Net.Internal.Http3
{
    // Creates the TLS session of each new connection on one HTTP/3 listener, and owns the
    // credentials those sessions share. The listener disposes it after its connections.
    internal sealed class EmbedIOQuicTlsFactory : IDisposable
    {
        private readonly ServerCertificateSource _certificates;
        private readonly ServerTlsPolicy? _policy;
        private readonly Func<X509Certificate2?, X509Certificate2Collection, bool>? _validate;
        private readonly ServerCredentialCache<SchannelTls.Credential>? _schannel;
        private readonly ServerCredentialCache<OpenSslTls.Credential>? _openSsl;

        internal EmbedIOQuicTlsFactory(ServerCertificateSource certificates, ServerTlsPolicy? policy)
        {
            _certificates = certificates;
            _policy = policy;
            _validate = policy?.RequestsClientCertificate == true
                ? (certificate, additional) => EmbedIOQuicTls.ValidateClientCertificate(policy, certificate, additional)
                : null;

            if (OperatingSystem.IsWindows()) _schannel = SchannelCredentials(certificates, _validate != null);

            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                _openSsl = new(certificates, OpenSslTls.Credential.Create);
        }

        // How many certificates have a cached credential.
        internal int CachedCredentials
        {
            get
            {
                if (OperatingSystem.IsWindows()) return _schannel?.Count ?? 0;

                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return _openSsl?.Count ?? 0;

                return 0;
            }
        }

        internal TlsSession Create(byte[] parameters)
        {
            // Capture the fallback per handshake, before a fragmented ClientHello
            // or a later publication can change this connection's selection. The
            // version comes first, so a publication between the two reads marks
            // this handshake as older than the certificate it captured.
            var version = _certificates.Version;
            var fallback = _certificates.Current;

            if (OperatingSystem.IsWindows() && _schannel != null)
                return CreateSchannel(_schannel, parameters, fallback, version);

            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && _openSsl != null)
                return CreateOpenSsl(_openSsl, parameters, fallback, version);

            throw new PlatformNotSupportedException("Recordless TLS requires a desktop platform.");
        }

        public void Dispose()
        {
            if (OperatingSystem.IsWindows()) _schannel?.Dispose();

            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) _openSsl?.Dispose();
        }

        [SupportedOSPlatform("windows")]
        private static ServerCredentialCache<SchannelTls.Credential> SchannelCredentials(
            ServerCertificateSource certificates,
            bool clientCertificates
        ) => new(certificates, certificate => SchannelTls.Credential.Acquire(certificate, clientCertificates));

        [SupportedOSPlatform("windows")]
        private SchannelTls CreateSchannel(
            ServerCredentialCache<SchannelTls.Credential> credentials,
            byte[] parameters,
            X509Certificate2 fallback,
            long version
        ) =>
            new(
                name => SelectCertificate(_policy, fallback, name),
                parameters,
                "h3",
                false,
                _validate,
                certificate => credentials.Lease(certificate, version)
            );

        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private OpenSslTls CreateOpenSsl(
            ServerCredentialCache<OpenSslTls.Credential> credentials,
            byte[] parameters,
            X509Certificate2 fallback,
            long version
        ) =>
            new(
                name => SelectCertificate(_policy, fallback, name),
                parameters,
                "h3",
                _validate,
                credentials: certificate => credentials.Lease(certificate, version)
            );

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
