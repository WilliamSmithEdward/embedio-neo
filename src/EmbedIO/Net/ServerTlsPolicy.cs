using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Configuration;
using EmbedIO.Diagnostics;

namespace EmbedIO.Net
{
    /// <summary>
    /// <para>TLS settings of HTTPS listeners: client certificates, the TLS protocol versions to allow and,
    /// on the .NET 10 asset, certificate selection by the server name a client asks for (SNI).</para>
    /// <para>A policy is locked when the server or listener that uses it is created.</para>
    /// </summary>
    /// <remarks>
    /// <para>With the default settings the policy changes nothing: no client certificate is requested,
    /// TLS 1.2 or later is allowed where the runtime supports it, and the configured server certificate is used.</para>
    /// <para>A listener refuses to start with a setting it cannot honor, rather than ignoring it; see
    /// the documentation of each property.</para>
    /// </remarks>
    public sealed class ServerTlsPolicy : ConfiguredObject
    {
        private const SslProtocols Tls13 = (SslProtocols)12288;

        private ClientCertificateMode _clientCertificateMode;
        private Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? _clientCertificateValidator;
        private bool _checkCertificateRevocation;
        private SslProtocols _enabledProtocols = SslProtocols.None;
        private Func<string?, X509Certificate2?>? _serverCertificateSelector;

        /// <summary>
        /// Gets or sets whether clients are asked for a certificate. The default is <see cref="Net.ClientCertificateMode.None"/>.
        /// </summary>
        /// <remarks>
        /// <para>Supported over TCP on both assets, and over QUIC by the default HTTP/3 binding.</para>
        /// <para>Handlers read the certificate with <see cref="HttpRequestExtensions.GetClientCertificate"/>.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set, and the policy is locked.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="Net.ClientCertificateMode"/>.</exception>
        public ClientCertificateMode ClientCertificateMode
        {
            get => _clientCertificateMode;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < ClientCertificateMode.None || value > ClientCertificateMode.Require)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _clientCertificateMode = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets the function that decides whether a client certificate is accepted.
        /// It receives the certificate, the chain the runtime built for it and the errors found while building
        /// and checking that chain, and returns <see langword="true"/> to accept the certificate.</para>
        /// <para>When this is <see langword="null"/>, the default, a certificate is accepted only if it has no
        /// errors: its chain ends at a root the operating system trusts.</para>
        /// </summary>
        /// <remarks>
        /// <para>The function runs during the handshake, for clients that send a certificate. Whether a client
        /// may connect without one is decided by <see cref="ClientCertificateMode"/>. An exception thrown by
        /// the function fails the handshake and is logged.</para>
        /// <para>The function may be called concurrently, and must not keep the chain, which the runtime disposes.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set, and the policy is locked.</exception>
        public Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? ClientCertificateValidator
        {
            get => _clientCertificateValidator;
            set
            {
                EnsureConfigurationNotLocked();
                _clientCertificateValidator = value;
            }
        }

        /// <summary>
        /// Gets or sets whether the revocation status of client certificates is checked online while
        /// validating them. The default is <see langword="false"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set, and the policy is locked.</exception>
        public bool CheckCertificateRevocation
        {
            get => _checkCertificateRevocation;
            set
            {
                EnsureConfigurationNotLocked();
                _checkCertificateRevocation = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets the TLS protocol versions that TCP listeners allow.
        /// The default, <see cref="SslProtocols.None"/>, allows supported TLS 1.2 and TLS 1.3; prefer it unless a
        /// requirement says otherwise.</para>
        /// <para>QUIC always uses TLS 1.3, so HTTP/3 listeners refuse to start with a value that excludes it.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set, and the policy is locked.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The mask contains a protocol other than TLS 1.2 or TLS 1.3.</exception>
        public SslProtocols EnabledProtocols
        {
            get => _enabledProtocols;
            set
            {
                EnsureConfigurationNotLocked();
                if (value != SslProtocols.None && value != SslProtocols.Tls12
                    && value != Tls13 && value != (SslProtocols.Tls12 | Tls13))
                    throw new ArgumentOutOfRangeException(nameof(value), "HTTPS requires TLS 1.2 or later and a known protocol mask.");
                _enabledProtocols = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets a function that chooses the server certificate for the host name a client asks for
        /// during the handshake (SNI). It receives that name, or <see langword="null"/> if the client sent none,
        /// and returns a certificate with its private key, or <see langword="null"/> to use the server's configured
        /// certificate.</para>
        /// </summary>
        /// <remarks>
        /// <para>Supported on the .NET 10 asset over TCP and by the default HTTP/3 binding. The .NET Standard 2.0
        /// asset and the native HTTP/3 binding refuse to start with a selector.</para>
        /// <para>The function may be called concurrently. An exception thrown by it fails the handshake and is logged.
        /// The application owns the certificates it returns.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set, and the policy is locked.</exception>
        public Func<string?, X509Certificate2?>? ServerCertificateSelector
        {
            get => _serverCertificateSelector;
            set
            {
                EnsureConfigurationNotLocked();
                _serverCertificateSelector = value;
            }
        }

        internal bool RequestsClientCertificate => _clientCertificateMode != ClientCertificateMode.None;

        internal void Lock() => LockConfiguration();

        // Called before a listener starts with this policy; refuses settings the listener cannot honor.
        internal void EnsureSupported(bool quic, bool nativeQuic)
        {
#if NET10_0_OR_GREATER
            // Both TCP and the default HTTP/3 binding support selection on this asset.
#else
            if (_serverCertificateSelector != null)
                throw new PlatformNotSupportedException("Selecting the server certificate by server name (SNI) requires the .NET 10 asset.");
#endif
            if (!quic)
                return;

            if (_enabledProtocols != SslProtocols.None && (_enabledProtocols & Tls13) == 0)
                throw new NotSupportedException("HTTP/3 requires TLS 1.3, which EnabledProtocols excludes.");

            if (nativeQuic && RequestsClientCertificate)
                throw new NotSupportedException("The native HTTP/3 binding does not support client certificates.");

            if (nativeQuic && _serverCertificateSelector != null)
                throw new NotSupportedException("The native HTTP/3 binding does not support selecting the server certificate by server name (SNI).");
        }

        // The runtime's validation callback for client certificates.
        internal bool ValidateClientCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate == null)
                return _clientCertificateMode == ClientCertificateMode.Allow;

            var validator = _clientCertificateValidator;
            if (validator == null)
                return errors == SslPolicyErrors.None;

            try
            {
                return validator(certificate as X509Certificate2 ?? new X509Certificate2(certificate), chain, errors);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                error.Log(nameof(ServerTlsPolicy), "The client certificate validator threw; the handshake was refused.");
                return false;
            }
        }

        // The certificate for a handshake that asked for a host name; the fallback when the selector declines.
        internal X509Certificate SelectServerCertificate(string? hostName, X509Certificate fallback)
        {
            var selector = _serverCertificateSelector;
            if (selector == null)
                return fallback;

            X509Certificate2? selected;
            try
            {
                selected = selector(string.IsNullOrEmpty(hostName) ? null : hostName);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                error.Log(nameof(ServerTlsPolicy), "The server certificate selector threw; the handshake was refused.");
                throw new AuthenticationException("The server certificate selector failed.", error);
            }

            return selected ?? fallback;
        }
    }
}
