using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;

namespace EmbedIO.Net
{
    /// <summary>The independently authored TCP HTTP listener with the existing public contract.</summary>
    public sealed class HttpListener : IHttpListener, IGracefulHttpListener
    {
        private readonly TcpListenerCoordinator _coordinator;
        /// <summary>Initializes a listener, optionally using the supplied server certificate.</summary>
        /// <param name="certificate">The borrowed server certificate.</param>
        public HttpListener(X509Certificate? certificate = null)
        {
            Certificate = certificate;
            _coordinator = new TcpListenerCoordinator(this);
        }
        /// <inheritdoc />
        public bool IgnoreWriteExceptions { get; set; } = true;
        // Set by a server with body policies: 100 Continue waits for the first body read.
        internal bool DeferContinue { get; set; }
        /// <inheritdoc />
        public bool IsListening => _coordinator.IsListening;
        /// <inheritdoc />
        public string Name { get; } = "Unosquare HTTP Listener";
        /// <inheritdoc />
        public List<string> Prefixes => _coordinator.PrefixSnapshot();
        internal X509Certificate? Certificate { get; }
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
        internal void RegisterContext(IHttpContextImpl context) => _coordinator.Register(context, null);
        internal void RegisterMultiplexedContext(IHttpContextImpl context, HttpConnection connection) => _coordinator.Register(context, connection);
        internal void UnregisterContext(IHttpContextImpl context) => _coordinator.Unregister(context);
        internal void AddConnection(HttpConnection connection) => _coordinator.AddSession(connection);
        internal void RemoveConnection(HttpConnection connection) => _coordinator.RemoveSession(connection);
    }
}
