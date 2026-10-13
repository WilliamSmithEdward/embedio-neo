using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    // An accept borrows one generation for its entire lifetime. Retirement first
    // seals producers, then cancels readers; the last reader releases wait resources.
    internal sealed class ListenerAcceptEpoch : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _wake = new(0);
        private readonly ConcurrentDictionary<string, IHttpContextImpl> _pending = new();
        private ConcurrentQueue<IHttpContextImpl> _order = new();
        // Tracks _pending's size: its IsEmpty and Count take every bucket lock,
        // which contends with all concurrent publications on each pulse.
        private int _pendingCount;
        private int _open;
        private int _producers;
        private int _readers;
        private int _waiters;
        private int _pulse;
        private int _retired;
        private int _cancelFinished;
        private int _released;

        internal ListenerAcceptEpoch() => StopToken = _stop.Token;
        internal CancellationToken StopToken { get; }
        internal bool Graceful { get; set; }
        internal bool Retired => Volatile.Read(ref _retired) != 0;
        internal bool AdmissionOpen => Volatile.Read(ref _open) != 0;

        internal void OpenAdmission() => Volatile.Write(ref _open, 1);
        internal void SealAdmission()
        {
            Interlocked.Exchange(ref _open, 0);
            var spin = default(SpinWait);
            while (Volatile.Read(ref _producers) != 0) spin.SpinOnce();
        }
        internal bool BorrowPublication()
        {
            Interlocked.Increment(ref _producers);
            if (AdmissionOpen) return true;
            ReturnPublication();
            return false;
        }
        internal void ReturnPublication() => Interlocked.Decrement(ref _producers);
        internal void Publish(IHttpContextImpl context)
        {
            if (!_pending.TryAdd(context.Id, context)) throw new InvalidOperationException("Unable to register context.");
            Interlocked.Increment(ref _pendingCount);
            Volatile.Read(ref _order).Enqueue(context);
            Interlocked.MemoryBarrier();
            Pulse();
        }

        internal bool BorrowReader()
        {
            Interlocked.Increment(ref _readers);
            if (!Retired) return true;
            ReturnReader();
            return false;
        }
        internal void ReturnReader()
        {
            if (Interlocked.Decrement(ref _readers) == 0) Reclaim();
        }
        internal void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) != 0) return;
            try { _stop.Cancel(); }
            finally
            {
                Volatile.Write(ref _cancelFinished, 1);
                Reclaim();
            }
        }
        public void Dispose()
        {
            SealAdmission();
            Retire();
        }
        private void Reclaim()
        {
            if (Volatile.Read(ref _cancelFinished) == 0 || Volatile.Read(ref _readers) != 0
                || Interlocked.CompareExchange(ref _released, 1, 0) != 0) return;
            _wake.Dispose();
            _stop.Dispose();
        }

        internal async Task<IHttpContextImpl> AcceptAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!cancellationToken.IsCancellationRequested && !StopToken.IsCancellationRequested && TryClaim(out var ready)) return ready;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, StopToken);
                try
                {
                    while (true)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref _waiters);
                        try
                        {
                            if (TryClaim(out var context)) return context;
                            await _wake.WaitAsync(linked.Token).ConfigureAwait(false);
                            Interlocked.Exchange(ref _pulse, 0);
                        }
                        finally { Interlocked.Decrement(ref _waiters); }
                        if (linked.IsCancellationRequested)
                        {
                            Pulse();
                            linked.Token.ThrowIfCancellationRequested();
                        }
                    }
                }
                catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
                { throw new OperationCanceledException("Accept canceled.", error, cancellationToken); }
                catch (OperationCanceledException)
                { throw Stopped(); }
            }
            finally { ReturnReader(); }
        }
        internal Exception Stopped() => Graceful ? new ListenerDrainedException()
            : new System.Net.HttpListenerException(995, "The listener stopped accepting requests.");
        private bool TryClaim([NotNullWhen(true)] out IHttpContextImpl? context)
        {
            while (Volatile.Read(ref _order).TryDequeue(out var candidate))
                if (((ICollection<KeyValuePair<string, IHttpContextImpl>>)_pending).Remove(new(candidate.Id, candidate)))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    context = candidate;
                    Pulse();
                    return true;
                }
            context = null;
            return false;
        }
        private void Pulse()
        {
            if (Volatile.Read(ref _waiters) != 0 && Volatile.Read(ref _pendingCount) > 0 && Interlocked.CompareExchange(ref _pulse, 1, 0) == 0)
                _wake.Release();
        }
        internal bool Withdraw(IHttpContextImpl context)
        {
            if (!((ICollection<KeyValuePair<string, IHttpContextImpl>>)_pending).Remove(new(context.Id, context))) return false;
            Interlocked.Decrement(ref _pendingCount);
            return true;
        }
        internal bool NeedsCompaction => Volatile.Read(ref _order).Count > Volatile.Read(ref _pendingCount) + 128;
        internal void Compact()
        {
            var replacement = new ConcurrentQueue<IHttpContextImpl>();
            foreach (var context in Volatile.Read(ref _order))
                if (_pending.TryGetValue(context.Id, out var current) && ReferenceEquals(current, context)) replacement.Enqueue(context);
            Volatile.Write(ref _order, replacement);
        }
        internal IHttpContextImpl[] DetachPending()
        {
            var retired = new List<IHttpContextImpl>();
            foreach (var entry in _pending)
                if (_pending.TryRemove(entry.Key, out var context))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    retired.Add(context);
                }
            while (Volatile.Read(ref _order).TryDequeue(out _)) { }
            return retired.ToArray();
        }
    }
}
