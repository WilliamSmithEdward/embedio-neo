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
    public sealed class HttpListener : IHttpListener, IGracefulHttpListener
    {
        private readonly SemaphoreSlim _ctxQueueSem = new(0);
        private readonly object _lifecycleSync = new();
        private CancellationTokenSource _acceptStop = new();
        private readonly ConcurrentDictionary<string, IHttpContextImpl> _ctxQueue;
        private readonly ConcurrentDictionary<HttpConnection, object> _connections;
        private readonly HttpListenerPrefixCollection _prefixes;
        private bool _disposed;
        private int _pendingAccepts;
        private Task? _drainTask;
        private HashSet<HttpConnection>? _drainConnections;
        private volatile bool _gracefulStop;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpListener" /> class.
        /// </summary>
        /// <param name="certificate">The certificate.</param>
        public HttpListener(X509Certificate? certificate = null)
        {
            Certificate = certificate;

            _prefixes = new HttpListenerPrefixCollection(this);
            _connections = new ConcurrentDictionary<HttpConnection, object>();
            _ctxQueue = new ConcurrentDictionary<string, IHttpContextImpl>();
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
                if (_drainTask != null && !_drainTask.IsCompleted)
                    throw new InvalidOperationException("The listener is still draining.");
                if (IsListening) return;
                _drainTask = null;
                _gracefulStop = false;
                if (_acceptStop.IsCancellationRequested)
                {
                    _acceptStop.Dispose();
                    _acceptStop = new CancellationTokenSource();
                }

                EndPointManager.AddListener(this);
                IsListening = true;
            }
        }

        Task IGracefulHttpListener.DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task drain;
            HashSet<HttpConnection>? owned;
            lock (_lifecycleSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(HttpListener));
                if (_drainTask == null)
                {
                    if (!IsListening) return Task.CompletedTask;
                    var connections = EndPointManager.BeginExclusiveDrain(this);
                    connections.UnionWith(_connections.Keys);
                    _drainConnections = connections;
                    _gracefulStop = true;
                    var deadline = new CancellationTokenSource(timeout);
                    _drainTask = Task.Run(() => DrainConnectionsAsync(connections, deadline));
                }
                drain = _drainTask;
                owned = _drainConnections;
            }
            return AwaitDrainAsync(drain, owned, cancellationToken);
        }

        private async Task AwaitDrainAsync(Task drain, HashSet<HttpConnection>? owned, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => AbortDrain(owned));
            await drain.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private void AbortDrain(HashSet<HttpConnection>? owned)
        {
            lock (_lifecycleSync)
                if (owned != null && ReferenceEquals(_drainConnections, owned)) Stop();
        }

        private async Task DrainConnectionsAsync(HashSet<HttpConnection> connections, CancellationTokenSource deadline)
        {
            using var deadlineLifetime = deadline;
            using var registration = deadline.Token.Register(() => AbortDrain(connections));
            try
            {
                // Do not hold the listener lock while entering protocol dispatch.
                await Task.WhenAll(connections.Select(connection => connection.DrainAsync())).ConfigureAwait(false);
            }
            finally
            {
                lock (_lifecycleSync)
                {
                    IsListening = false;
                    if (!_disposed)
                    {
                        _acceptStop.Cancel();
                        Close();
                    }
                    _drainConnections = null;
                }
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
        public void AddPrefix(string urlPrefix)
        {
            lock (_lifecycleSync)
            {
                if (_drainTask != null && !_drainTask.IsCompleted)
                    throw new InvalidOperationException("Cannot add a prefix while the listener is draining.");
                _prefixes.Add(urlPrefix);
            }
        }

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
                    if (_pendingAccepts == 0) DisposeAcceptResources();
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
                _pendingAccepts++;
            }

            try
            {
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

                                foreach (var entry in _ctxQueue)
                                {
                                    if (_ctxQueue.TryRemove(entry.Key, out var context)) return context;
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
                        if (_gracefulStop) throw new ListenerDrainedException();
                        throw new HttpListenerException(995, "The listener stopped accepting requests.");
                    }
                }
            }
            finally
            {
                lock (_lifecycleSync)
                {
                    // Cancellation completes asynchronously; wait until no semaphore operation remains.
                    if (--_pendingAccepts == 0 && _disposed) DisposeAcceptResources();
                }
            }
        }

        private void DisposeAcceptResources()
        {
            _ctxQueueSem.Dispose();
            _acceptStop.Dispose();
        }

        internal void RegisterContext(IHttpContextImpl context)
        {
            lock (_lifecycleSync)
            {
                if (_disposed || !IsListening)
                    throw new HttpListenerException(995, "The listener stopped accepting requests.");
                if (_drainConnections != null && (context is not HttpListenerContext http1 || !_drainConnections.Contains(http1.Connection)))
                    throw new HttpListenerException(995, "The listener is draining.");
                if (!_ctxQueue.TryAdd(context.Id, context))
                    throw new InvalidOperationException("Unable to register context");
                _ = _ctxQueueSem.Release();
            }
        }

        internal void RegisterMultiplexedContext(IHttpContextImpl context, HttpConnection connection)
        {
            lock (_lifecycleSync)
            {
                if (_disposed || !IsListening) throw new HttpListenerException(995, "The listener stopped accepting requests.");
                if (_drainConnections != null && !_drainConnections.Contains(connection))
                    throw new HttpListenerException(995, "The listener is draining.");
                _connections[connection] = connection;
                if (!_ctxQueue.TryAdd(context.Id, context)) throw new InvalidOperationException("Unable to register context.");
                _ = _ctxQueueSem.Release();
            }
        }

        internal void UnregisterContext(IHttpContextImpl context) => _ctxQueue.TryRemove(context.Id, out _);

        internal void AddConnection(HttpConnection cnc) => _connections[cnc] = cnc;

        internal void RemoveConnection(HttpConnection cnc) => _connections.TryRemove(cnc, out _);

        private void Close()
        {
            EndPointManager.RemoveListener(this);

            var connections = _connections.ToArray();
            _connections.Clear();
            for (var i = connections.Length - 1; i >= 0; i--)
            {
                connections[i].Key.Close(true);
            }

            while (!_ctxQueue.IsEmpty)
            {
                foreach (var entry in _ctxQueue)
                {
                    // A previously closed connection cannot unbind its context again.
                    if (_ctxQueue.TryRemove(entry.Key, out var context))
                    {
                        if (context is HttpListenerContext http1) http1.Connection.Close(true);
                        // HTTP/2 contexts finish via their canceled connection dispatch.

                    }
                }
            }
        }
    }
}
