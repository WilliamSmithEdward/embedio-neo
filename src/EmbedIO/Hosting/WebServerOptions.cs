using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using EmbedIO.Diagnostics;
using EmbedIO.Utilities;

namespace EmbedIO
{
    /// <summary>
    /// Contains options for configuring an instance of <see cref="WebServer"/>.
    /// </summary>
    public sealed class WebServerOptions : WebServerOptionsBase
    {
        private const string NetShLogSource = "NetSh";
        private readonly List<string> _urlPrefixes = [];
        private HttpListenerMode _mode = HttpListenerMode.EmbedIO;
        private X509Certificate2? _certificate;
        private Net.ServerCertificateSource? _certificateSource;
        private Net.ServerTlsPolicy? _tlsPolicy;
        private WebTransportOptions? _webTransport;
        private bool _requireHttp3AddressValidation;
        private bool _advertiseHttp3 = true;
        private string? _certificateThumbprint;
        private bool _autoLoadCertificate;
        private bool _autoRegisterCertificate;
        private StoreName _storeName = StoreName.My;
        private StoreLocation _storeLocation = StoreLocation.LocalMachine;

        /// <summary>
        /// Gets the URL prefixes.
        /// </summary>
        // A wrapper, not the list itself: a caller could otherwise cast the view back to
        // List<string> and add prefixes after the options are locked.
        public IReadOnlyList<string> UrlPrefixes => _urlPrefixesView ??= _urlPrefixes.AsReadOnly();

        private System.Collections.ObjectModel.ReadOnlyCollection<string>? _urlPrefixesView;

        /// <summary>
        /// Gets or sets the type of HTTP listener.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        /// <seealso cref="HttpListenerMode"/>
        public HttpListenerMode Mode
        {
            get => _mode;
            set
            {
                EnsureConfigurationNotLocked();
                _mode = value;
            }
        }

        /// <summary>
        /// Gets or sets the X.509 certificate to use for SSL connections.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        public X509Certificate2? Certificate
        {
            get
            {
                if (AutoRegisterCertificate)
                    return TryRegisterCertificate() ? _certificate : null;

                return _certificate ?? (AutoLoadCertificate ? LoadCertificate() : null);
            }
            set
            {
                EnsureConfigurationNotLocked();
                _certificate = value;
            }
        }

        /// <summary>
        /// <para>Gets or sets a source of the server certificate that the application can update while the server runs,
        /// for example to install a renewed certificate. Each new TLS handshake, over TCP or QUIC, uses the source's
        /// current certificate.</para>
        /// <para>Configure either this property or <see cref="Certificate"/>, <see cref="CertificateThumbprint"/>,
        /// <see cref="AutoLoadCertificate"/> and <see cref="AutoRegisterCertificate"/>, not both.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        /// <seealso cref="Net.ServerCertificateSource"/>
        public Net.ServerCertificateSource? CertificateSource
        {
            get => _certificateSource;
            set
            {
                EnsureConfigurationNotLocked();
                _certificateSource = value;
            }
        }

        /// <summary>
        /// Gets or sets the TLS settings of HTTPS listeners: client certificates, TLS protocol versions and
        /// certificate selection by server name. The default, <see langword="null"/>, keeps the defaults of
        /// <see cref="Net.ServerTlsPolicy"/>. The policy is locked with these options.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        /// <seealso cref="Net.ServerTlsPolicy"/>
        public Net.ServerTlsPolicy? TlsPolicy
        {
            get => _tlsPolicy;
            set
            {
                EnsureConfigurationNotLocked();
                _tlsPolicy = value;
            }
        }

        /// <summary>
        /// Gets or sets the WebTransport settings. The default, <see langword="null"/>, serves no WebTransport.
        /// The settings are locked with these options.
        /// </summary>
        /// <remarks>WebTransport needs <see cref="HttpListenerMode.EmbedIOHttp3"/> or
        /// <see cref="HttpListenerMode.EmbedIOCombined"/> on the .NET 10 asset; other modes refuse to start with it.</remarks>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        /// <seealso cref="IWebTransportContext"/>
        public WebTransportOptions? WebTransport
        {
            get => _webTransport;
            set
            {
                EnsureConfigurationNotLocked();
                _webTransport = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether HTTP/3 makes each new client prove its address
        /// before any TLS work starts. The default is <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// <para>When enabled, the server answers a new connection's first packet with a Retry
        /// carrying an address-validation token (RFC 9000 section 8.1.2) and keeps no state for it;
        /// the handshake starts only when the client returns the token from the same IP address.
        /// This stops spoofed source addresses from consuming TLS work, at the cost of one more
        /// round trip for every new connection.</para>
        /// <para>It needs <see cref="HttpListenerMode.EmbedIOHttp3"/> or
        /// <see cref="HttpListenerMode.EmbedIOCombined"/> on the .NET 10 asset; other modes refuse to
        /// start with it.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        public bool RequireHttp3AddressValidation
        {
            get => _requireHttp3AddressValidation;
            set
            {
                EnsureConfigurationNotLocked();
                _requireHttp3AddressValidation = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether <see cref="HttpListenerMode.EmbedIOCombined"/> advertises its
        /// HTTP/3 endpoint to clients. The default is <see langword="true"/>.
        /// </summary>
        /// <remarks>
        /// <para>Every response then carries <c>Alt-Svc: h3=":port"; ma=86400</c>, naming the port the request
        /// arrived on, which serves TCP and QUIC alike. Clients that honor it, such as browsers and .NET clients
        /// with <c>RequestVersionOrHigher</c>, move to HTTP/3 for later requests. An <c>Alt-Svc</c> header that
        /// the application sets is sent unchanged.</para>
        /// <para>The other modes send no Alt-Svc header: EmbedIO serves no HTTP/3, and EmbedIOHttp3 serves
        /// nothing else.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        public bool AdvertiseHttp3
        {
            get => _advertiseHttp3;
            set
            {
                EnsureConfigurationNotLocked();
                _advertiseHttp3 = value;
            }
        }

        /// <summary>
        /// Gets or sets the thumbprint of the X.509 certificate to use for SSL connections.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        public string? CertificateThumbprint
        {
            get => _certificateThumbprint;
            set
            {
                EnsureConfigurationNotLocked();

                // strip any non-hexadecimal values and make uppercase
                _certificateThumbprint = value == null
                    ? null
                    : Regex
                        .Replace(value, @"[^\da-fA-F]", string.Empty)
                        .ToUpper(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to automatically load the X.509 certificate.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        /// <exception cref="PlatformNotSupportedException">This property is being set to <see langword="true"/>
        /// and the underlying operating system is not Windows.</exception>
        public bool AutoLoadCertificate
        {
            get => _autoLoadCertificate;
            set
            {
                EnsureConfigurationNotLocked();

                if (value && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    throw new PlatformNotSupportedException(
                        "AutoLoadCertificate functionality is only available under Windows."
                    );

                _autoLoadCertificate = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to automatically bind the X.509 certificate
        /// to the port used for HTTPS.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        /// <exception cref="PlatformNotSupportedException">This property is being set to <see langword="true"/>
        /// and the underlying operating system is not Windows.</exception>
        public bool AutoRegisterCertificate
        {
            get => _autoRegisterCertificate;
            set
            {
                EnsureConfigurationNotLocked();

                if (value && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    throw new PlatformNotSupportedException(
                        "AutoRegisterCertificate functionality is only available under Windows."
                    );

                _autoRegisterCertificate = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating the X.509 certificate store where to load the certificate from.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        /// <seealso cref="System.Security.Cryptography.X509Certificates.StoreName"/>
        public StoreName StoreName
        {
            get => _storeName;
            set
            {
                EnsureConfigurationNotLocked();
                _storeName = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating the location of the X.509 certificate store where to load the certificate from.
        /// </summary>
        /// <exception cref="InvalidOperationException">This property is being set,
        /// and this instance's configuration is locked.</exception>
        /// <seealso cref="System.Security.Cryptography.X509Certificates.StoreLocation"/>
        public StoreLocation StoreLocation
        {
            get => _storeLocation;
            set
            {
                EnsureConfigurationNotLocked();
                _storeLocation = value;
            }
        }

        /// <inheritdoc />
        protected override void OnBeforeLockConfiguration()
        {
            // Checked on the fields: the Certificate getter loads or registers certificates.
            if (
                _certificateSource != null
                && (_certificate != null
                    || _certificateThumbprint != null
                    || _autoLoadCertificate
                    || _autoRegisterCertificate)
            )
            {
                throw new InvalidOperationException(
                    "Configure either CertificateSource or Certificate, CertificateThumbprint, AutoLoadCertificate and AutoRegisterCertificate, not both."
                );
            }

            _tlsPolicy?.Lock();
            _webTransport?.Lock();
            base.OnBeforeLockConfiguration();
        }

        private int? _maximumConnections;
        private TimeSpan? _requestHeadersTimeout;
        private TimeSpan? _keepAliveTimeout;

        /// <summary>
        /// Gets or sets the maximum number of TCP connections the server keeps open at once.
        /// The default is null, preserving existing unlimited behavior.
        /// </summary>
        /// <remarks>
        /// <para>A connection accepted while the limit is reached is closed at once, before any
        /// TLS handshake or HTTP response; it is counted again when an open connection closes.
        /// The limit covers HTTP/1.1 and HTTP/2 connections of the managed TCP listener, across
        /// all of this server's endpoints.</para>
        /// <para>Connection limits apply to the <see cref="HttpListenerMode.EmbedIO"/> and
        /// <see cref="HttpListenerMode.EmbedIOCombined"/> modes; HTTP/3 connections are not
        /// counted. A server with connection limits cannot share a port with another server.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public int? MaximumConnections
        {
            get => _maximumConnections;
            set
            {
                EnsureConfigurationNotLocked();

                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));

                _maximumConnections = value;
            }
        }

        /// <summary>
        /// Gets or sets how long a new TCP connection may take to deliver its first complete
        /// request head, including the TLS handshake, and how long each HTTP/3 stream may take to
        /// deliver its header. The default is null, which applies 30 seconds.
        /// </summary>
        /// <remarks>
        /// <para>When the deadline passes on TCP the connection is closed without a response. On
        /// HTTP/2 the deadline covers the connection preface. Subsequent requests on the same
        /// connection are governed by <see cref="KeepAliveTimeout"/>.</para>
        /// <para>On HTTP/3 the deadline applies to every stream the client opens, from the time the
        /// server accepts it: a request stream must deliver its complete headers, and any other
        /// stream its stream header. A request stream that misses it is reset with
        /// H3_REQUEST_REJECTED, so the client can retry the request, and a unidirectional stream
        /// with H3_STREAM_CREATION_ERROR. Deadlines are checked once a second.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds the
        /// largest supported timer interval.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public TimeSpan? RequestHeadersTimeout
        {
            get => _requestHeadersTimeout;
            set
            {
                EnsureConfigurationNotLocked();
                _requestHeadersTimeout = ValidateTimeout(value);
            }
        }

        /// <summary>
        /// Gets or sets how long an idle TCP connection stays open between requests. The
        /// default is null, preserving existing behavior: 15 seconds on HTTP/1.1 and no idle
        /// deadline on HTTP/2.
        /// </summary>
        /// <remarks>
        /// <para>On HTTP/1.1 the deadline runs from the end of a response until the next request
        /// head is complete, and the <c>Keep-Alive</c> response header advertises it in whole
        /// seconds, rounded up. When it passes the connection is closed.</para>
        /// <para>On HTTP/2 the deadline runs while the connection has no open stream. When it
        /// passes the server sends GOAWAY and closes the connection gracefully.</para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive or exceeds the
        /// largest supported timer interval.</exception>
        /// <exception cref="InvalidOperationException">Configuration is locked.</exception>
        public TimeSpan? KeepAliveTimeout
        {
            get => _keepAliveTimeout;
            set
            {
                EnsureConfigurationNotLocked();
                _keepAliveTimeout = ValidateTimeout(value);
            }
        }

        internal bool HasConnectionLimits =>
            _maximumConnections.HasValue || _requestHeadersTimeout.HasValue || _keepAliveTimeout.HasValue;

        // The limits only TCP listeners can apply; RequestHeadersTimeout also bounds HTTP/3 streams.
        internal bool HasTcpOnlyConnectionLimits => _maximumConnections.HasValue || _keepAliveTimeout.HasValue;

        // The listener-level policy; the shared default when no limit is configured.
        internal Net.Internal.TcpConnectionPolicy CreateConnectionPolicy() =>
            HasConnectionLimits
                ? new Net.Internal.TcpConnectionPolicy(
                    _maximumConnections,
                    _requestHeadersTimeout.HasValue
                        ? (int)_requestHeadersTimeout.Value.TotalMilliseconds
                        : Net.Internal.TcpConnectionPolicy.DefaultHeaderTimeoutMilliseconds,
                    _keepAliveTimeout.HasValue ? (int)_keepAliveTimeout.Value.TotalMilliseconds : null
                )
                : Net.Internal.TcpConnectionPolicy.Default;

        private static TimeSpan? ValidateTimeout(TimeSpan? value)
        {
            // Whole milliseconds from 1 to int.MaxValue: the range a timer deadline accepts.
            if (value.HasValue && (value.Value.TotalMilliseconds < 1 || value.Value.TotalMilliseconds > int.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(value));

            return value;
        }

        /// <summary>
        /// Adds a URL prefix.
        /// </summary>
        /// <param name="urlPrefix">The URL prefix.</param>
        /// <exception cref="InvalidOperationException">This instance's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="urlPrefix"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="urlPrefix"/> is the empty string.</para>
        /// <para>- or -</para>
        /// <para><paramref name="urlPrefix"/> is already registered.</para>
        /// </exception>
        public void AddUrlPrefix(string urlPrefix)
        {
            EnsureConfigurationNotLocked();

            urlPrefix = Validate.NotNullOrEmpty(nameof(urlPrefix), urlPrefix);

            if (_urlPrefixes.Contains(urlPrefix))
                throw new ArgumentException("URL prefix is already registered.", nameof(urlPrefix));

            _urlPrefixes.Add(urlPrefix);
        }

        /// <inheritdoc cref="AddUrlPrefix(string)"/>
        public void AddUrlPrefix(Uri urlPrefix) =>
            AddUrlPrefix(Validate.NotNull(nameof(urlPrefix), urlPrefix).OriginalString);

        private X509Certificate2? LoadCertificate()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            if (!string.IsNullOrWhiteSpace(_certificateThumbprint)) return GetCertificate(_certificateThumbprint);

            using var netsh = GetNetsh("show");

            string? thumbprint = null;

            netsh.ErrorDataReceived += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                e.Data.Error(NetShLogSource);
            };

            netsh.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                e.Data.Debug(NetShLogSource);

                var line = e.Data.Trim();

                if (line.StartsWith("Certificate Hash") && line.IndexOf(":", StringComparison.Ordinal) > -1)
                    thumbprint = line
                        .Split(':')[1]
                        .Trim();
            };

            if (!netsh.Start())
                return null;

            netsh.BeginOutputReadLine();
            netsh.BeginErrorReadLine();
            netsh.WaitForExit();

            return netsh.ExitCode == 0 && !string.IsNullOrEmpty(thumbprint) ? GetCertificate(thumbprint) : null;
        }

        private X509Certificate2? GetCertificate(string? thumbprint = null)
        {
            using var store = new X509Store(StoreName, StoreLocation);
            store.Open(OpenFlags.ReadOnly);
            var signingCert = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                thumbprint
                    ?? _certificateThumbprint
                    ?? throw new InvalidOperationException("The certificate thumbprint has not been configured."),
                false
            );
            return signingCert.Count == 0 ? null : signingCert[0];
        }

        private bool AddCertificateToStore()
        {
            using var store = new X509Store(StoreName, StoreLocation);

            try
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(
                    _certificate ?? throw new InvalidOperationException("The certificate has not been configured.")
                );
                return true;
            }

            catch (Exception error) when (
                error is System.Security.Cryptography.CryptographicException
                    or System.Security.SecurityException
                    or UnauthorizedAccessException
                    or InvalidOperationException
            )
            {
                return false;
            }
        }

        private bool TryRegisterCertificate()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            if (_certificate == null)
                throw new InvalidOperationException("A certificate is required to AutoRegister");

            if (GetCertificate(_certificate.Thumbprint) == null && !AddCertificateToStore())
            {
                throw new InvalidOperationException(
                    "The provided certificate cannot be added to the default store, add it manually"
                );
            }

            using var netsh = GetNetsh(
                "add",
                $"certhash={_certificate.Thumbprint} appid={{adaa04bb-8b63-4073-a12f-d6f8c0b4383f}}"
            );

            var sb = new StringBuilder();

            void PushLine(object sender, DataReceivedEventArgs e)
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                sb.AppendLine(e.Data);
                e.Data.Error(NetShLogSource);
            }

            netsh.OutputDataReceived += PushLine;

            netsh.ErrorDataReceived += PushLine;

            if (!netsh.Start()) return false;

            netsh.BeginOutputReadLine();
            netsh.BeginErrorReadLine();
            netsh.WaitForExit();

            return netsh.ExitCode == 0 ? true : throw new InvalidOperationException($"NetSh error: {sb}");
        }

        private int GetSslPort()
        {
            var port = 443;

            foreach (var url in UrlPrefixes.Where(x => x.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
            {
                var match = Regex.Match(
                    url,
                    @"^https://(?:\[[^\]]+\]|[^/:]+):(\d+)(?:/|$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                );

                if (match.Success && int.TryParse(match.Groups[1].Value, out port))
                    break;
            }

            return port;
        }

        private Process GetNetsh(string verb, string options = "") => new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                Arguments = $"http {verb} sslcert ipport=0.0.0.0:{GetSslPort()} {options}"
            }
        };
    }
}
