using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EmbedIO.Quic
{
    // Only the local validation host creates named disposable software keys.
    // Production callers retain their existing certificate/key storage policy.
    [SupportedOSPlatform("windows")]
    internal sealed class SchannelTestCertificate : IDisposable
    {
        private readonly string _name = "EmbedIOQuic-wire-"
            + Guid
                .NewGuid()
                .ToString("N");
        private readonly CngKey _key;
        private readonly X509Certificate2 _certificate;
        private bool _disposed;

        internal SchannelTestCertificate(string publicCertificatePath, bool elliptic)
        {
            _key = CngKey.Create(
                elliptic ? CngAlgorithm.ECDsaP256 : CngAlgorithm.Rsa,
                _name,
                new CngKeyCreationParameters
                {
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing
                }
            );

            try
            {
                using AsymmetricAlgorithm key = elliptic ? new ECDsaCng(_key) : new RSACng(_key);

                try
                {
                    var unexpected = key.ExportPkcs8PrivateKey();
                    CryptographicOperations.ZeroMemory(unexpected);
                    throw new InvalidOperationException("Synthetic private key unexpectedly permits export.");
                }

                catch (CryptographicException) { }

                var request = key is RSA rsa
                    ? new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
                    : new CertificateRequest("CN=localhost", (ECDsa)key, HashAlgorithmName.SHA256);
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                _certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow.AddHours(1)
                );
                File.WriteAllText(publicCertificatePath, _certificate.ExportCertificatePem());
            }

            catch
            {
                _certificate?.Dispose();
                _key.Delete();
                _key.Dispose();
                throw;
            }
        }

        internal TlsSession Create(byte[] parameters, string protocol)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new SchannelTls(_certificate, parameters, protocol);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _certificate.Dispose();

            if (!StringComparer.Ordinal.Equals(_key.KeyName, _name))
                throw new InvalidOperationException("Unexpected owned key name.");

            _key.Delete();
            _key.Dispose();

            if (CngKey.Exists(_name)) throw new InvalidOperationException("Synthetic key cleanup failed.");
        }
    }
}
