using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace EmbedIO.Quic
{
    // A validation-host owner. Production certificate policy is integrated separately.
    internal sealed class DevelopmentTlsProvider : IDisposable
    {
        private readonly string _library;
        private readonly string _certificate;
        private readonly string _key;
        private readonly SchannelTestCertificate? _nativeCertificate;
        private readonly SchannelTestCertificate? _sniCertificate;
        private bool _disposed;

        internal DevelopmentTlsProvider(string library, string certificate, string key)
        {
            _library = library;
            _certificate = certificate;
            _key = key;

            if (library is "schannel-rsa" or "schannel-ecdsa" or "schannel-sni")
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Schannel validation requires Windows.");

                _nativeCertificate = new SchannelTestCertificate(certificate, library == "schannel-ecdsa");

                if (library == "schannel-sni")
                {
                    try
                    {
                        _sniCertificate = new SchannelTestCertificate(
                            certificate + ".selected.pem",
                            true,
                            "selected.example"
                        );
                    }

                    catch
                    {
                        _nativeCertificate.Dispose();
                        throw;
                    }
                }
            }
        }

        internal X509Certificate2? NativeCertificate =>
            OperatingSystem.IsWindows() ? _nativeCertificate?.BorrowCertificate() : null;

        internal TlsSession Create(byte[] parameters, string protocol)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (OperatingSystem.IsWindows() && _nativeCertificate != null)
            {
                if (_sniCertificate != null)
                    return new SchannelTls(SelectSniCertificate, parameters, protocol);

                return _nativeCertificate.Create(parameters, protocol);
            }

            return new OpenSslTls(_library, _certificate, _key, parameters, protocol);
        }

        [SupportedOSPlatform("windows")]
        private X509Certificate2 SelectSniCertificate(string? name) =>
            (StringComparer.OrdinalIgnoreCase.Equals(name, "selected.example")
                ? _sniCertificate
                : _nativeCertificate)?.BorrowCertificate()
                ?? throw new InvalidOperationException("Missing SNI test credential.");

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            if (OperatingSystem.IsWindows() && _nativeCertificate != null)
            {
                try
                {
                    _sniCertificate?.Dispose();
                }

                finally
                {
                    _nativeCertificate.Dispose();
                }

                QuicFuzzChecks.Require(
                    SchannelTls.ActiveCredentials == 0
                        && SchannelTls.ActiveContexts == 0
                        && SchannelTls.ActiveRegions == 0,
                    "native-tls-owned-resources-released"
                );
            }
        }
    }
}
