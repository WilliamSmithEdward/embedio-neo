using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Never runs application callbacks on a transport, shutdown, or deadline callback stack.
    // Completion and callback exit jointly own disposal, so even a blocked callback cannot
    // hold up connection retirement or allow a successor to reuse its cancellation source.
    internal sealed class RequestAbortToken : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _parent;
        private readonly Action<Exception> _report;
        private readonly CancellationToken _parentToken;
        private bool _aborted;
        private bool _completed;
        private bool _callbacksDone;
        private bool _disposed;
        internal RequestAbortToken(CancellationToken parent, Action<Exception> report)
        {
            _report = report;
            Token = _source.Token;
            _parentToken = parent;
            // Internal parent notification must not capture a tenant's AsyncLocal/Activity
            // or execute its context-change notifications on transport cancellation stacks.
            if (ExecutionContext.IsFlowSuppressed()) _parent = Register(parent);
            else { using (ExecutionContext.SuppressFlow()) _parent = Register(parent); }
        }
        private CancellationTokenRegistration Register(CancellationToken parent)
            => parent.Register(static state => ((RequestAbortToken)(state ?? throw new InvalidOperationException())).Abort(), this);
        internal CancellationToken Token { get; }
        internal bool IsAborted { get { lock (_sync) return _aborted; } }
        internal void Abort()
        {
            lock (_sync)
            {
                if (_completed || _aborted) return;
                _aborted = true;
            }
            if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(Cancel);
            else { using (ExecutionContext.SuppressFlow()) _ = Task.Run(Cancel); }
        }
        private void Cancel()
        {
            try { _source.Cancel(); }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                // Callback exceptions must not escape an unobserved worker or interrupt cleanup.
                try { _report(error); }
                catch (Exception diagnostic) when (ExceptionPolicy.IsRecoverable(diagnostic)) { }
            }
            finally { lock (_sync) _callbacksDone = true; Release(); }
        }
        internal void Complete()
        {
            if (_parentToken.IsCancellationRequested) Abort();
            lock (_sync) _completed = true;
            Release();
        }
        public void Dispose() => Complete();
        private void Release()
        {
            lock (_sync)
            {
                if (_disposed || !_completed || (_aborted && !_callbacksDone)) return;
                _disposed = true;
            }
            _parent.Dispose();
            // A retained canceled token may still register callbacks or use its wait handle.
            // Register can observe not-canceled, race Cancel/Dispose, then return without
            // invoking its callback when it observes the disposed source. Keep canceled
            // sources alive with their tokens; GC reclaims their managed state and any
            // lazily allocated wait handle. No timer or parent registration is retained.
            if (!_aborted) _source.Dispose();
        }
    }
}
