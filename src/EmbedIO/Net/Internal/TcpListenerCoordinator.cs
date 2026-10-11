using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // Owns cold listener transitions. Accept generations and the endpoint registry
    // own their separate resources; public compatibility stays in the facade.
    internal sealed class TcpListenerCoordinator : IDisposable
    {
        private const int Ready = 0, Running = 1, Draining = 2, Stopped = 3, Disposed = 4;
        private readonly HttpListener _owner;
        private readonly object _gate = new();
        private readonly List<string> _prefixes = new();
        private readonly ConcurrentDictionary<HttpConnection, object> _sessions = new();
        private ListenerAcceptEpoch _epoch = new();
        private int _stage = Ready;
        private Task? _drain;
        private HashSet<HttpConnection>? _drainSessions;
        private HashSet<HttpConnection>? _drainAdmissions;

        internal TcpListenerCoordinator(HttpListener owner) => _owner = owner;
        internal bool IsListening => Volatile.Read(ref _stage) is Running or Draining;
        internal List<string> PrefixSnapshot() { lock (_gate) return new List<string>(_prefixes); }

        internal void AddPrefix(string text)
        {
            lock (_gate)
            {
                if (_drain != null && !_drain.IsCompleted) throw new InvalidOperationException("Cannot add a prefix while the listener is draining.");
                ListenerPrefix.CheckUri(text);
                if (_prefixes.Contains(text)) return;
                if (IsListening) EndPointManager.AddPrefix(text, _owner);
                _prefixes.Add(text);
            }
        }
        internal void Start()
        {
            lock (_gate)
            {
                if (_stage == Disposed) throw new ObjectDisposedException(nameof(HttpListener));
                if (_drain != null && !_drain.IsCompleted) throw new InvalidOperationException("The listener is still draining.");
                if (IsListening) return;
                _drain = null;
                if (_epoch.Retired) Volatile.Write(ref _epoch, new ListenerAcceptEpoch());
                EndPointManager.AddListener(_owner);
                Volatile.Write(ref _stage, Running);
                _epoch.OpenAdmission();
            }
        }
        internal void Stop(bool dispose)
        {
            lock (_gate)
            {
                if (_stage == Disposed) return;
                Volatile.Write(ref _stage, dispose ? Disposed : Stopped);
                _epoch.SealAdmission();
                try { _epoch.Retire(); }
                finally { ReleaseSessions(); }
            }
        }
        public void Dispose()
        {
            try { Stop(true); }
            finally { _epoch.Dispose(); }
        }
        internal Task<IHttpContextImpl> AcceptAsync(CancellationToken cancellationToken)
        {
            ListenerAcceptEpoch epoch;
            while (true)
            {
                if (Volatile.Read(ref _stage) == Disposed) return Task.FromException<IHttpContextImpl>(new ObjectDisposedException(nameof(HttpListener)));
                if (cancellationToken.IsCancellationRequested)
                {
                    // Use the public async-task builder to retain the original
                    // OperationCanceledException and canceled task state together.
                    var canceled = AsyncTaskMethodBuilder<IHttpContextImpl>.Create();
                    canceled.SetException(new OperationCanceledException("Accept canceled.", cancellationToken));
                    return canceled.Task;
                }
                epoch = Volatile.Read(ref _epoch);
                if (!epoch.BorrowReader()) return Task.FromException<IHttpContextImpl>(epoch.Stopped());
                if (ReferenceEquals(epoch, Volatile.Read(ref _epoch))) break;
                epoch.ReturnReader();
            }
            return epoch.AcceptAsync(cancellationToken);
        }
        internal void Register(IHttpContextImpl context, HttpConnection? multiplexed)
        {
            var epoch = Volatile.Read(ref _epoch);
            if (epoch.BorrowPublication())
            {
                try
                {
                    if (multiplexed != null) _sessions[multiplexed] = multiplexed;
                    epoch.Publish(context);
                }
                finally { epoch.ReturnPublication(); }
                return;
            }
            lock (_gate)
            {
                if (!IsListening) throw new System.Net.HttpListenerException(995, "The listener stopped accepting requests.");
                var connection = multiplexed ?? (context as HttpListenerContext)?.Connection;
                if (_stage == Draining && (connection == null || _drainAdmissions?.Contains(connection) != true))
                    throw new System.Net.HttpListenerException(995, "The listener is draining.");
                if (multiplexed != null) _sessions[multiplexed] = multiplexed;
                _epoch.Publish(context);
            }
        }
        internal void Unregister(IHttpContextImpl context)
        {
            var epoch = Volatile.Read(ref _epoch);
            if (!epoch.Withdraw(context) || !epoch.NeedsCompaction) return;
            lock (_gate)
            {
                if (!ReferenceEquals(epoch, _epoch) || !epoch.NeedsCompaction) return;
                var reopen = epoch.AdmissionOpen;
                epoch.SealAdmission();
                try { epoch.Compact(); }
                finally { if (reopen) epoch.OpenAdmission(); }
            }
        }
        internal void AddSession(HttpConnection connection) => _sessions[connection] = connection;
        internal void RemoveSession(HttpConnection connection) => _sessions.TryRemove(connection, out _);
        internal Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task drain;
            HashSet<HttpConnection>? sessions;
            TaskCompletionSource<bool>? completion = null;
            CancellationTokenSource? deadline = null;
            lock (_gate)
            {
                if (_stage == Disposed) throw new ObjectDisposedException(nameof(HttpListener));
                if (_drain == null)
                {
                    if (!IsListening) return Task.CompletedTask;
                    _epoch.SealAdmission();
                    var owned = EndPointManager.BeginDrain(_owner, out var exclusive);
                    owned.UnionWith(_sessions.Keys);
                    _drainSessions = owned;
                    _drainAdmissions = new HashSet<HttpConnection>(owned.Where(connection => exclusive.Contains(connection.Endpoint)));
                    _epoch.Graceful = true;
                    Volatile.Write(ref _stage, Draining);
                    deadline = new CancellationTokenSource(timeout);
                    completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _drain = completion.Task;
                }
                drain = _drain;
                sessions = _drainSessions;
            }
            if (completion != null && deadline != null && sessions != null)
            {
                // Enter each protocol's drain before returning to the caller, but
                // outside the listener gate: protocol dispatch can register here.
                // Scheduling this entry as a worker left a window for new streams.
                var protocolDrain = FinishDrainAsync(sessions, deadline);
                _ = protocolDrain.ContinueWith(static (task, state) =>
                {
                    var result = (TaskCompletionSource<bool>)(state ?? throw new InvalidOperationException("Missing drain completion."));
                    if (task.IsFaulted)
                    {
                        var failure = task.Exception ?? new AggregateException("Drain fault did not expose its cause.");
                        result.TrySetException(failure.InnerExceptions);
                    }
                    else if (task.IsCanceled) result.TrySetCanceled();
                    else result.TrySetResult(true);
                }, completion, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return ObserveDrainAsync(drain, sessions, cancellationToken);
        }
        private async Task ObserveDrainAsync(Task drain, HashSet<HttpConnection>? sessions, CancellationToken token)
        {
            using var registration = token.Register(() => AbortDrain(sessions));
            await drain.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        private void AbortDrain(HashSet<HttpConnection>? sessions)
        {
            lock (_gate)
                if (sessions != null && ReferenceEquals(sessions, _drainSessions)) Stop(false);
        }
        private async Task FinishDrainAsync(HashSet<HttpConnection> sessions, CancellationTokenSource deadline)
        {
            using var ownedDeadline = deadline;
            using var registration = deadline.Token.Register(() => AbortDrain(sessions));
            try { await Task.WhenAll(sessions.Select(connection => connection.DrainForListenerAsync(_owner))).ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    if (_stage != Disposed)
                    {
                        Volatile.Write(ref _stage, Stopped);
                        try { _epoch.Retire(); }
                        finally { ReleaseSessions(); }
                    }
                    _drainSessions = null;
                    _drainAdmissions = null;
                }
            }
        }
        private void ReleaseSessions()
        {
            ExceptionDispatchInfo? failure = null;
            Attempt(() => EndPointManager.RemoveListener(_owner));
            var sessions = _sessions.Keys.ToArray();
            _sessions.Clear();
            foreach (var connection in sessions) Attempt(() => connection.CloseForListener(_owner));
            foreach (var context in _epoch.DetachPending())
                if (context is HttpListenerContext http1) Attempt(() => http1.Connection.CloseForListener(_owner));
            failure?.Throw();
            void Attempt(Action release)
            {
                try { release(); }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { failure ??= ExceptionDispatchInfo.Capture(error); }
            }
        }
    }
}
