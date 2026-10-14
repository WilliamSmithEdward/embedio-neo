using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;

namespace EmbedIO.Net
{
    /// <summary>The independently authored TCP HTTP listener with the existing public contract.</summary>
    public sealed class HttpListener : IHttpListener, IGracefulHttpListener, IDispatchingHttpListener
    {
        private readonly TcpListenerCoordinator _coordinator;
        /// <summary>Initializes a listener, optionally using the supplied server certificate.</summary>
        /// <param name="certificate">The borrowed server certificate.</param>
        public HttpListener(X509Certificate? certificate = null) : this(certificate, null) { }
        /// <summary>Creates a listener whose TLS handshakes use the current certificate of <paramref name="certificateSource"/>.</summary>
        /// <param name="certificateSource">The borrowed source of the server certificate, which the application may update while the listener runs.</param>
        /// <returns>A listener that borrows <paramref name="certificateSource"/> and its certificates.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="certificateSource"/> is <see langword="null"/>.</exception>
        public static HttpListener CreateWithCertificateSource(ServerCertificateSource certificateSource)
            => new HttpListener(null, certificateSource ?? throw new ArgumentNullException(nameof(certificateSource)), null);
        /// <summary>Initializes a listener whose TLS handshakes use the current certificate of <paramref name="certificateSource"/>
        /// and the settings of <paramref name="tlsPolicy"/>.</summary>
        /// <param name="certificateSource">The borrowed source of the server certificate, which the application may update while the listener runs.</param>
        /// <param name="tlsPolicy">The TLS settings, or <see langword="null"/> for the defaults. The policy is locked.</param>
        /// <exception cref="ArgumentNullException"><paramref name="certificateSource"/> is <see langword="null"/>.</exception>
        /// <exception cref="PlatformNotSupportedException"><paramref name="tlsPolicy"/> selects certificates by server name,
        /// which requires the .NET 10 asset.</exception>
        public HttpListener(ServerCertificateSource certificateSource, ServerTlsPolicy? tlsPolicy)
            : this(null, certificateSource ?? throw new ArgumentNullException(nameof(certificateSource)), null)
        {
            tlsPolicy?.EnsureSupported(false, false);
            tlsPolicy?.Lock();
            TlsPolicy = tlsPolicy;
        }
        internal HttpListener(X509Certificate? certificate, EmbedIO.Internal.RequestPolicyResolver? bodyPolicy) : this(certificate, null, bodyPolicy) { }
        internal HttpListener(ServerCertificateSource? certificateSource, EmbedIO.Internal.RequestPolicyResolver? bodyPolicy) : this(null, certificateSource, bodyPolicy) { }
        private HttpListener(X509Certificate? certificate, ServerCertificateSource? certificateSource, EmbedIO.Internal.RequestPolicyResolver? bodyPolicy)
        {
            BodyPolicy = bodyPolicy;
            _certificate = certificate;
            _certificateSource = certificateSource;
            _coordinator = new TcpListenerCoordinator(this);
        }
        private readonly X509Certificate? _certificate;
        private readonly ServerCertificateSource? _certificateSource;
        /// <inheritdoc />
        public bool IgnoreWriteExceptions { get; set; } = true;
        // Set by a server with body policies: 100 Continue waits for the first body read.
        internal bool DeferContinue { get; set; }
        // Connection limits of the endpoints this listener opens. Set before Start.
        internal Internal.TcpConnectionPolicy ConnectionPolicy { get; set; } = Internal.TcpConnectionPolicy.Default;
        // Records the connections of the endpoints this listener opens. Set before Start.
        internal EmbedIO.Internal.ServerTelemetry? Telemetry { get; set; }
        /// <inheritdoc />
        public bool IsListening => _coordinator.IsListening;
        /// <inheritdoc />
        public string Name { get; } = "Unosquare HTTP Listener";
        /// <inheritdoc />
        public List<string> Prefixes => _coordinator.PrefixSnapshot();
        // Read for every new connection, so a published replacement applies to the next handshake.
        internal X509Certificate? Certificate => _certificateSource?.Current ?? _certificate;
        // Whether a secure prefix can ever be served: a certificate or a source was supplied.
        internal bool HasServerCertificate => _certificateSource != null || _certificate != null;
        internal bool UsesCertificateSource => _certificateSource != null;
        // Whether two listeners present the same server certificate: the same fixed certificate
        // (compared by hash, so a certificate loaded twice from the same bytes matches) or the
        // same certificate source instance. A fixed certificate and a source never match, because
        // the source may rotate away from that certificate while the endpoint is shared.
        internal bool SharesCertificateWith(HttpListener other)
        {
            if (_certificateSource != null || other._certificateSource != null)
                return ReferenceEquals(_certificateSource, other._certificateSource);
            if (ReferenceEquals(_certificate, other._certificate)) return true;
            if (_certificate == null || other._certificate == null) return false;
            var mine = _certificate.GetCertHash();
            var theirs = other._certificate.GetCertHash();
            if (mine.Length != theirs.Length) return false;
            for (var index = 0; index < mine.Length; index++)
                if (mine[index] != theirs[index]) return false;
            return true;
        }
        internal EmbedIO.Internal.RequestPolicyResolver? BodyPolicy { get; }
        // TLS settings of the endpoints this listener opens. Set before Start.
        internal ServerTlsPolicy? TlsPolicy { get; set; }
        /// <inheritdoc />
        public void Start() => _coordinator.Start();
        /// <inheritdoc />
        public void Stop() => _coordinator.Stop(false);
        /// <inheritdoc />
        public void AddPrefix(string urlPrefix) => _coordinator.AddPrefix(urlPrefix);
        /// <inheritdoc />
        public void Dispose() => _coordinator.Dispose();
        /// <inheritdoc />
        public Task<IHttpContextImpl> GetContextAsync(CancellationToken cancellationToken) => _coordinator.AcceptAsync(cancellationToken);
        Task IGracefulHttpListener.DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => _coordinator.DrainAsync(timeout, cancellationToken);
        void IDispatchingHttpListener.SetDispatcher(Func<IHttpContextImpl, Task> dispatcher) => _coordinator.SetDispatcher(dispatcher);
        // Both return the handler's task when the server dispatches directly, or null when the request was queued for GetContextAsync.
        internal Task? RegisterContext(IHttpContextImpl context) => _coordinator.Register(context, null);
        internal Task? RegisterMultiplexedContext(IHttpContextImpl context, HttpConnection connection) => _coordinator.Register(context, connection);
        internal void UnregisterContext(IHttpContextImpl context) => _coordinator.Unregister(context);
        internal void AddConnection(HttpConnection connection) => _coordinator.AddSession(connection);
        internal void RemoveConnection(HttpConnection connection) => _coordinator.RemoveSession(connection);
    }
}
