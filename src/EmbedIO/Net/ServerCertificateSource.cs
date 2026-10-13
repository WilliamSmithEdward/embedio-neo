using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace EmbedIO.Net
{
    /// <summary>
    /// <para>Holds the server certificate of HTTPS listeners and lets the application replace it while they run.</para>
    /// <para>Each new TLS handshake, over TCP or QUIC, uses the certificate that is current when it starts.
    /// Connections that are already established keep the certificate they started with.</para>
    /// </summary>
    /// <remarks>
    /// <para>The application owns the certificates. This class never disposes one, and a replaced certificate
    /// may still be in use by connections that started before the replacement; dispose it, if at all,
    /// only after the servers that use this source have stopped.</para>
    /// <para>All members are thread-safe.</para>
    /// </remarks>
    public sealed class ServerCertificateSource
    {
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

        private readonly object _publishSync = new object();
        private X509Certificate2 _current;
        private long _version;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServerCertificateSource"/> class.
        /// </summary>
        /// <param name="certificate">The initial certificate.</param>
        /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="certificate"/> is not usable as a server certificate;
        /// see <see cref="Publish"/>.</exception>
        public ServerCertificateSource(X509Certificate2 certificate)
            : this(Validated(certificate), 1)
        {
        }

        private ServerCertificateSource(X509Certificate2? certificate, long version)
        {
            _current = certificate ?? throw new ArgumentNullException(nameof(certificate));
            _version = version;
        }

        /// <summary>
        /// Gets the certificate that new handshakes use.
        /// </summary>
        public X509Certificate2 Current => Volatile.Read(ref _current);

        // Increments on every publication; lets a listener notice a replacement without comparing certificates.
        internal long Version => Interlocked.Read(ref _version);

        /// <summary>
        /// <para>Replaces the certificate that new handshakes use.</para>
        /// <para>The certificate must have an accessible private key, must be valid at the time of the call,
        /// and, if it restricts its key usage, must allow server authentication. A certificate that fails
        /// these checks is rejected and the current one stays in use.</para>
        /// </summary>
        /// <param name="certificate">The new certificate.</param>
        /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="certificate"/> is not usable as a server certificate.</exception>
        public void Publish(X509Certificate2 certificate)
        {
            Validate(certificate, nameof(certificate));
            lock (_publishSync)
            {
                Volatile.Write(ref _current, certificate);
                Interlocked.Increment(ref _version);
            }
        }

        // A fixed certificate configured the existing way keeps its existing behavior: no new checks.
        internal static ServerCertificateSource Fixed(X509Certificate2 certificate) => new ServerCertificateSource(certificate, 1);

        private static X509Certificate2 Validated(X509Certificate2 certificate)
        {
            Validate(certificate, nameof(certificate));
            return certificate;
        }

        private static void Validate(X509Certificate2 certificate, string parameterName)
        {
            if (certificate == null)
                throw new ArgumentNullException(parameterName);

            if (!certificate.HasPrivateKey)
                throw new ArgumentException("The certificate has no private key.", parameterName);

            var now = DateTime.Now;
            if (certificate.NotBefore > now)
                throw new ArgumentException($"The certificate is not valid before {certificate.NotBefore:O}.", parameterName);

            if (certificate.NotAfter < now)
                throw new ArgumentException($"The certificate expired on {certificate.NotAfter:O}.", parameterName);

            foreach (var extension in certificate.Extensions)
            {
                if (extension is not X509EnhancedKeyUsageExtension usage)
                    continue;

                var allowed = false;
                foreach (var oid in usage.EnhancedKeyUsages)
                {
                    if (oid.Value == ServerAuthenticationOid)
                        allowed = true;
                }

                if (!allowed)
                    throw new ArgumentException("The certificate's enhanced key usage does not include server authentication.", parameterName);
            }

            if (!HasUsablePrivateKey(certificate, out var error))
                throw new ArgumentException("The certificate's private key cannot be used.", parameterName, error);
        }

        private static bool HasUsablePrivateKey(X509Certificate2 certificate, out Exception? error)
        {
            error = null;
            try
            {
                using (var rsa = certificate.GetRSAPrivateKey())
                {
                    if (rsa != null)
                        return true;
                }

                using (var ecdsa = certificate.GetECDsaPrivateKey())
                {
                    if (ecdsa != null)
                        return true;
                }

                return false;
            }
            catch (CryptographicException exception)
            {
                error = exception;
                return false;
            }
        }
    }
}
