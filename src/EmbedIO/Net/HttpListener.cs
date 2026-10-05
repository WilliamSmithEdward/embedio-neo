using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;
using HttpListenerException = System.Net.HttpListenerException;

namespace EmbedIO.Net
{
    /// <summary>
    /// The EmbedIO implementation of the standard HTTP Listener class.
    ///
    /// Based on MONO HttpListener class.
    /// </summary>
    /// <seealso cref="IDisposable" />
    public sealed class HttpListener : IHttpListener
    {
        private readonly SemaphoreSlim _ctxQueueSem = new(0);
        private readonly object _lifecycleSync = new();
        private CancellationTokenSource _acceptStop = new();
        private readonly ConcurrentDictionary<string, HttpListenerContext> _ctxQueue;
        private readonly ConcurrentDictionary<HttpConnection, object> _connections;
        private readonly HttpListenerPrefixCollection _prefixes;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpListener" /> class.
        /// </summary>
        /// <param name="certificate">The certificate.</param>
        public HttpListener(X509Certificate? certificate = null)
        {
            Certificate = certificate;

            _prefixes = new HttpListenerPrefixCollection(this);
            _connections = new ConcurrentDictionary<HttpConnection, object>();
            _ctxQueue = new ConcurrentDictionary<string, HttpListenerContext>();
        }

        /// <inheritdoc />
        public bool IgnoreWriteExceptions { get; set; } = true;

        /// <inheritdoc />
        public bool IsListening { get; private set; }

        /// <inheritdoc />
        public string Name { get; } = "Unosquare HTTP Listener";

        /// <inheritdoc />
        public List<string> Prefixes => _prefixes.ToList();

        /// <summary>
        /// Gets the certificate.
        /// </summary>
        /// <value>
        /// The certificate.
        /// </value>
        internal X509Certificate? Certificate { get; }

        /// <inheritdoc />
        public void Start()
        {
            lock (_lifecycleSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(HttpListener));
                if (IsListening) return;
                if (_acceptStop.IsCancellationRequested)
                {
                    _acceptStop.Dispose();
                    _acceptStop = new CancellationTokenSource();
                }

                EndPointManager.AddListener(this);
                IsListening = true;
            }
        }

        /// <inheritdoc />
        public void Stop()
        {
            lock (_lifecycleSync)
            {
                if (_disposed) return;
                IsListening = false;
                _acceptStop.Cancel();
                Close();
            }
        }

        /// <inheritdoc />
        public void AddPrefix(string urlPrefix) => _prefixes.Add(urlPrefix);

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_lifecycleSync)
            {
                if (_disposed) return;
                _disposed = true;
                IsListening = false;
                // Disposing SemaphoreSlim alone does not complete its pending waits.
                _acceptStop.Cancel();
                try { Close(); }
                finally
                {
                    _ctxQueueSem.Dispose();
                    _acceptStop.Dispose();
                }
            }
        }

        /// <inheritdoc />
        public async Task<IHttpContextImpl> GetContextAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource linked;
            lock (_lifecycleSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(HttpListener));
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _acceptStop.Token);
            }

            using (linked)
            {
                try
                {
                    while (true)
                    {
                        await _ctxQueueSem.WaitAsync(linked.Token).ConfigureAwait(false);
                        lock (_lifecycleSync)
                        {
                            if (linked.IsCancellationRequested)
                            {
                                // A canceled accept must not consume another waiter's queue signal.
                                if (!_disposed) _ = _ctxQueueSem.Release();
                                linked.Token.ThrowIfCancellationRequested();
                            }

                            foreach (var key in _ctxQueue.Keys)
                            {
                                if (_ctxQueue.TryRemove(key, out var context)) return context;
                            }
                        }
                    }
                }
                catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Accept canceled.", error, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new HttpListenerException(995, "The listener stopped accepting requests.");
                }
            }
        }

        internal void RegisterContext(HttpListenerContext context)
        {
            lock (_lifecycleSync)
            {
                if (_disposed || !IsListening)
                    throw new HttpListenerException(995, "The listener stopped accepting requests.");
                if (!_ctxQueue.TryAdd(context.Id, context))
                    throw new InvalidOperationException("Unable to register context");
                _ = _ctxQueueSem.Release();
            }
        }

        internal void UnregisterContext(HttpListenerContext context) => _ctxQueue.TryRemove(context.Id, out _);

        internal void AddConnection(HttpConnection cnc) => _connections[cnc] = cnc;

        internal void RemoveConnection(HttpConnection cnc) => _connections.TryRemove(cnc, out _);

        private void Close()
        {
            EndPointManager.RemoveListener(this);

            var keys = _connections.Keys;
            var connections = new HttpConnection[keys.Count];
            keys.CopyTo(connections, 0);
            _connections.Clear();
            var list = new List<HttpConnection>(connections);

            for (var i = list.Count - 1; i >= 0; i--)
            {
                list[i].Close(true);
            }

            while (!_ctxQueue.IsEmpty)
            {
                foreach (var key in _ctxQueue.Keys.ToArray())
                {
                    // A previously closed connection cannot unbind its context again.
                    if (_ctxQueue.TryRemove(key, out var context))
                    {
                        context.Connection.Close(true);
                    }
                }
            }
        }
    }
}
