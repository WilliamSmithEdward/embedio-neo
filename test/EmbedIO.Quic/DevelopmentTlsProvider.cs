namespace EmbedIO.Quic
{
    // A validation-host owner. Production certificate policy is integrated separately.
    internal sealed class DevelopmentTlsProvider : IDisposable
    {
        private readonly string _library;
        private readonly string _certificate;
        private readonly string _key;
        private readonly SchannelTestCertificate? _nativeCertificate;
        private bool _disposed;

        internal DevelopmentTlsProvider(string library, string certificate, string key)
        {
            _library = library;
            _certificate = certificate;
            _key = key;

            if (library is "schannel-rsa" or "schannel-ecdsa")
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Schannel validation requires Windows.");

                _nativeCertificate = new SchannelTestCertificate(certificate, library == "schannel-ecdsa");
            }
        }

        internal TlsSession Create(byte[] parameters, string protocol)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (OperatingSystem.IsWindows() && _nativeCertificate != null)
                return _nativeCertificate.Create(parameters, protocol);

            return new OpenSslTls(_library, _certificate, _key, parameters, protocol);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            if (OperatingSystem.IsWindows() && _nativeCertificate != null)
            {
                _nativeCertificate.Dispose();
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
