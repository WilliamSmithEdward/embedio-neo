using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Retirement keeps the semaphore alive until every admitted writer,
    // including queued writers, exits. One state word counts admitted writers
    // and records retirement, so entering and leaving take no lock, and an
    // uncontended asynchronous entry completes without allocating.
    internal sealed class AsyncWriteGate : IDisposable
    {
        private const int Retired = int.MinValue;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _state;

        internal Scope Enter()
        {
            if (!TryAddReference()) throw new ObjectDisposedException(nameof(AsyncWriteGate));

            try
            {
                _semaphore.Wait();
                return new Scope(this);
            }

            catch
            {
                ReleaseReference();
                throw;
            }
        }

        internal ValueTask<Scope> EnterAsync(CancellationToken token)
        {
            // Retirement is reported through the returned task, as an asynchronous entry always has.
            if (!TryAddReference())
                return new ValueTask<Scope>(
                    Task.FromException<Scope>(new ObjectDisposedException(nameof(AsyncWriteGate)))
                );

            try
            {
                if (!token.IsCancellationRequested && _semaphore.Wait(0, CancellationToken.None))
                    return new ValueTask<Scope>(new Scope(this));
            }

            catch
            {
                ReleaseReference();
                throw;
            }

            return WaitAsync(token);
        }

        // Intentional shutdown may submit already accepted output, but must never queue
        // behind another writer before closing the transport that can unblock that writer.
        internal bool TryEnter(out Scope scope)
        {
            scope = default;

            if (!TryAddReference()) return false;

            var entered = false;

            try
            {
                entered = _semaphore.Wait(0, CancellationToken.None);

                if (entered) scope = new Scope(this);

                return entered;
            }

            finally
            {
                if (!entered) ReleaseReference();
            }
        }

        private async ValueTask<Scope> WaitAsync(CancellationToken token)
        {
            try
            {
                await _semaphore
                    .WaitAsync(token)
                    .ConfigureAwait(false);
                return new Scope(this);
            }

            catch
            {
                ReleaseReference();
                throw;
            }
        }

        public void Dispose()
        {
            var state = Volatile.Read(ref _state);

            while (state >= 0)
            {
                var observed = Interlocked.CompareExchange(ref _state, state | Retired, state);

                if (observed == state)
                {
                    if (state == 0) _semaphore.Dispose();

                    return;
                }

                state = observed;
            }
        }

        private void Release()
        {
            _semaphore.Release();
            ReleaseReference();
        }

        private bool TryAddReference()
        {
            var state = Volatile.Read(ref _state);

            while (state >= 0)
            {
                var observed = Interlocked.CompareExchange(ref _state, state + 1, state);

                if (observed == state) return true;

                state = observed;
            }

            return false;
        }

        // The last writer to leave a retired gate disposes the semaphore.
        private void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _state) == Retired) _semaphore.Dispose();
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly AsyncWriteGate _gate;

            internal Scope(AsyncWriteGate gate) => _gate = gate;

            public void Dispose() => _gate.Release();
        }
    }
}
