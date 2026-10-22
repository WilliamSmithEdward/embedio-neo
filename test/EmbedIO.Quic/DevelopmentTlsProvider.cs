using System.Runtime.Versioning;
using System.Security.Cryptography;
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
        private readonly X509Certificate2? _portableCertificate;
        private readonly X509Certificate2? _portableSelectedCertificate;

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

            else if (library is "portable-rsa" or "portable-sni")
            {
                if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                    throw new PlatformNotSupportedException("The portable TLS fixture requires Unix.");

                _portableCertificate = CreatePortableCertificate(certificate, key, "localhost");

                if (library == "portable-sni")
                {
                    try
                    {
                        _portableSelectedCertificate = CreatePortableCertificate(
                            certificate + ".selected.pem",
                            key + ".selected.pem",
                            "selected.example"
                        );
                    }

                    catch
                    {
                        _portableCertificate.Dispose();
                        throw;
                    }
                }
            }
        }

        internal X509Certificate2? NativeCertificate =>
            _portableCertificate ?? (OperatingSystem.IsWindows() ? _nativeCertificate?.BorrowCertificate() : null);
        internal X509Certificate2? SelectedNativeCertificate =>
            _portableSelectedCertificate ?? (OperatingSystem.IsWindows() ? _sniCertificate?.BorrowCertificate() : null);

        internal TlsSession Create(byte[] parameters, string protocol)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (OperatingSystem.IsWindows() && _nativeCertificate != null)
            {
                if (_sniCertificate != null)
                    return new SchannelTls(SelectSniCertificate, parameters, protocol);

                return _nativeCertificate.Create(parameters, protocol);
            }

            if (_portableCertificate != null && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                return new OpenSslTls(
                    name => StringComparer.OrdinalIgnoreCase.Equals(name, "selected.example")
                        ? _portableSelectedCertificate ?? _portableCertificate
                        : _portableCertificate,
                    parameters,
                    protocol,
                    recordDiagnostics: true
                );

            return new DevelopmentOpenSslTls(_library, _certificate, _key, parameters, protocol);
        }

        internal static X509Certificate2 CreatePortableCertificate(string path, string keyPath, string name)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + name,
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(name);

            if (name == "localhost")
            {
                names.AddIpAddress(System.Net.IPAddress.Loopback);
                names.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            }

            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], true));
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1)
            );

            try
            {
                File.WriteAllText(path, certificate.ExportCertificatePem());
                File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
                return certificate;
            }

            catch
            {
                certificate.Dispose();
                throw;
            }
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
            _portableSelectedCertificate?.Dispose();
            _portableCertificate?.Dispose();

            if (_portableCertificate != null && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                QuicFuzzChecks.Require(
                    OpenSslTls.ActiveContexts == 0 && OpenSslTls.ActiveBlocks == 0,
                    "portable-native-tls-owned-resources-released"
                );

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
