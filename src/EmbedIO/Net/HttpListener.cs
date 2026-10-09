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
        // Pending contexts by ID; removing an entry claims it for accept, unregistration or Close.
        private readonly ConcurrentDictionary<string, IHttpContextImpl> _ctxQueue;
        // Arrival order for accept. Entries already claimed elsewhere are skipped.
        private ConcurrentQueue<IHttpContextImpl> _ctxOrder = new();
        private const int WithdrawnQueueSlack = 128;
        // 1 while running and not draining: registration may then skip _lifecycleSync.
        // Lifecycle changes clear it and wait for _admissionsInFlight to reach zero
        // before they snapshot or close, so no lock-free registration overlaps them.
        private int _fastAdmission;
        private int _admissionsInFlight;
        // Accepts about to wait on _ctxQueueSem. Its permits are wake-ups, not a context count.
        private int _waitingAccepts;
        // One outstanding wakeup is shared by queue bursts and listener generations.
        private int _wakePending;
        private readonly ConcurrentDictionary<HttpConnection, object> _connections;
        private readonly HttpListenerPrefixCollection _prefixes;
        private bool _disposed;
        private int _pendingAccepts;
        private Task? _drainTask;
        private HashSet<HttpConnection>? _drainConnections;
        private HashSet<HttpConnection>? _drainAdmissionConnections;
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
                Volatile.Write(ref _fastAdmission, 1);
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
                    // The snapshot below must include every HTTP/2 connection admitted so far.
                    CloseFastAdmission();
                    var connections = EndPointManager.BeginDrain(this, out var exclusiveEndpoints);
                    connections.UnionWith(_connections.Keys);
                    _drainConnections = connections;
                    _drainAdmissionConnections = new HashSet<HttpConnection>(connections.Where(connection => exclusiveEndpoints.Contains(connection.Endpoint)));
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
                await Task.WhenAll(connections.Select(connection => connection.DrainForListenerAsync(this))).ConfigureAwait(false);
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
                    _drainAdmissionConnections = null;
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
                CloseFastAdmission();
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
                CloseFastAdmission();
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
            CancellationToken acceptStop;
            lock (_lifecycleSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(HttpListener));
                acceptStop = _acceptStop.Token;
                _pendingAccepts++;
            }

            try
            {
                // Under load a context is usually queued already; take it without linking tokens.
                if (!cancellationToken.IsCancellationRequested && !acceptStop.IsCancellationRequested
                    && TryTakeQueuedContext(out var queued))
                    return queued;

                // While this accept is pending, Dispose leaves the semaphore and token source alive.
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, acceptStop))
                {
                    try
                    {
                        while (true)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            // Announce the wait before checking the queue again. Interlocked
                            // increment is a full fence: a registration either is seen by this
                            // check or sees the count and releases the semaphore.
                            _ = Interlocked.Increment(ref _waitingAccepts);
                            try
                            {
                                if (TryTakeQueuedContext(out var context)) return context;
                                await _ctxQueueSem.WaitAsync(linked.Token).ConfigureAwait(false);
                                _ = Interlocked.Exchange(ref _wakePending, 0);
                            }
                            finally { _ = Interlocked.Decrement(ref _waitingAccepts); }

                            if (linked.IsCancellationRequested)
                            {
                                // A canceled accept must not consume another waiter's queue signal.
                                SignalAccept();
                                linked.Token.ThrowIfCancellationRequested();
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

        private bool TryTakeQueuedContext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IHttpContextImpl? context)
        {
            while (Volatile.Read(ref _ctxOrder).TryDequeue(out var candidate))
            {
                // Unregistration and Close remove their contexts from _ctxQueue first.
                if (((ICollection<KeyValuePair<string, IHttpContextImpl>>)_ctxQueue).Remove(new KeyValuePair<string, IHttpContextImpl>(candidate.Id, candidate)))
                {
                    context = candidate;
                    SignalAccept();
                    return true;
                }
            }

            context = null;
            return false;
        }

        internal void RegisterContext(IHttpContextImpl context)
        {
            if (TryAdmitWithoutLock(context, null, "Unable to register context")) return;
            lock (_lifecycleSync)
            {
                if (_disposed || !IsListening)
                    throw new HttpListenerException(995, "The listener stopped accepting requests.");
                if (_drainConnections != null && (context is not HttpListenerContext http1 || _drainAdmissionConnections?.Contains(http1.Connection) != true))
                    throw new HttpListenerException(995, "The listener is draining.");
                Enqueue(context, "Unable to register context");
            }
        }

        internal void RegisterMultiplexedContext(IHttpContextImpl context, HttpConnection connection)
        {
            if (TryAdmitWithoutLock(context, connection, "Unable to register context.")) return;
            lock (_lifecycleSync)
            {
                if (_disposed || !IsListening) throw new HttpListenerException(995, "The listener stopped accepting requests.");
                if (_drainConnections != null && _drainAdmissionConnections?.Contains(connection) != true)
                    throw new HttpListenerException(995, "The listener is draining.");
                _connections[connection] = connection;
                Enqueue(context, "Unable to register context.");
            }
        }

        // Admits a context while running and not draining. Returns false when the
        // caller must take the locked path, which applies the stop and drain rules.
        private bool TryAdmitWithoutLock(IHttpContextImpl context, HttpConnection? multiplexed, string duplicateMessage)
        {
            // Interlocked increment is a full fence: either this read sees a closed
            // gate, or CloseFastAdmission sees this admission and waits for it.
            _ = Interlocked.Increment(ref _admissionsInFlight);
            try
            {
                if (Volatile.Read(ref _fastAdmission) == 0) return false;
                if (multiplexed != null && !_connections.ContainsKey(multiplexed)) _connections[multiplexed] = multiplexed;
                Enqueue(context, duplicateMessage);
                return true;
            }
            finally { _ = Interlocked.Decrement(ref _admissionsInFlight); }
        }

        private void Enqueue(IHttpContextImpl context, string duplicateMessage)
        {
            if (!_ctxQueue.TryAdd(context.Id, context)) throw new InvalidOperationException(duplicateMessage);
            _ctxOrder.Enqueue(context);
            // The semaphore only wakes accepts that announced a wait. A busy accept loop
            // finds the context on its next check, so registrations skip the semaphore lock.
            // The queue publishes the item with a release write; the fence keeps this read
            // after it, pairing with the increment in GetContextAsync.
            Interlocked.MemoryBarrier();
            SignalAccept();
        }

        private void SignalAccept()
        {
            // Coalesce permits while a waiter is paused before its queue check.
            // Successful claims relay the wakeup while queued work remains.
            if (Volatile.Read(ref _waitingAccepts) != 0 && !_ctxQueue.IsEmpty
                && Interlocked.CompareExchange(ref _wakePending, 1, 0) == 0)
                _ = _ctxQueueSem.Release();
        }

        // Callers hold _lifecycleSync. Admissions without the lock never block or
        // run application code, so this wait is short.
        private void CloseFastAdmission()
        {
            _ = Interlocked.Exchange(ref _fastAdmission, 0);
            var spin = default(SpinWait);
            while (Volatile.Read(ref _admissionsInFlight) != 0) spin.SpinOnce();
        }

        internal void UnregisterContext(IHttpContextImpl context)
        {
            if (!_ctxQueue.TryRemove(context.Id, out _)) return;
            // Accepted requests are already absent from the claim map, so this
            // cold path runs only when queued work is withdrawn before accept.
            if (Volatile.Read(ref _ctxOrder).Count <= _ctxQueue.Count + WithdrawnQueueSlack) return;
            lock (_lifecycleSync)
            {
                var previous = Volatile.Read(ref _ctxOrder);
                if (previous.Count <= _ctxQueue.Count + WithdrawnQueueSlack) return;
                var reopen = Volatile.Read(ref _fastAdmission) != 0;
                CloseFastAdmission();
                try
                {
                    var compacted = new ConcurrentQueue<IHttpContextImpl>();
                    foreach (var candidate in previous)
                        if (_ctxQueue.TryGetValue(candidate.Id, out var pending) && ReferenceEquals(candidate, pending))
                            compacted.Enqueue(candidate);
                    // A racing accept still claims from the dictionary exactly
                    // once. Its copied queue entry is then harmlessly skipped.
                    Volatile.Write(ref _ctxOrder, compacted);
                }
                finally { if (reopen) Volatile.Write(ref _fastAdmission, 1); }
            }
        }

        internal void AddConnection(HttpConnection cnc) => _connections[cnc] = cnc;

        internal void RemoveConnection(HttpConnection cnc) => _connections.TryRemove(cnc, out _);

        private void Close()
        {
            EndPointManager.RemoveListener(this);

            var connections = _connections.ToArray();
            _connections.Clear();
            for (var i = connections.Length - 1; i >= 0; i--)
            {
                connections[i].Key.CloseForListener(this);
            }

            while (!_ctxQueue.IsEmpty)
            {
                foreach (var entry in _ctxQueue)
                {
                    // A previously closed connection cannot unbind its context again.
                    if (_ctxQueue.TryRemove(entry.Key, out var context))
                    {
                        if (context is HttpListenerContext http1) http1.Connection.ForceClose();
                        // HTTP/2 contexts finish via their canceled connection dispatch.

                    }
                }
            }

            // Every entry left in arrival order is now claimed; release the references.
            while (_ctxOrder.TryDequeue(out _)) { }
        }
    }
}
