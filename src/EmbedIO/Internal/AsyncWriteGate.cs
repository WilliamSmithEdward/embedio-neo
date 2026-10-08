using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Disposal waits for every admitted writer, including queued writers, to exit.
    internal sealed class AsyncWriteGate : IDisposable
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly Action _release;
        private int _writers;
        private bool _disposed;

        internal AsyncWriteGate() => _release = Release;

        internal async Task<Scope> EnterAsync(CancellationToken token)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AsyncWriteGate));
                _writers++;
            }
            try
            {
                await _semaphore.WaitAsync(token).ConfigureAwait(false);
                return new Scope(_release);
            }
            catch
            {
                ReleaseReference();
                throw;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_writers == 0) _semaphore.Dispose();
            }
        }

        private void Release()
        {
            _semaphore.Release();
            ReleaseReference();
        }

        private void ReleaseReference()
        {
            lock (_sync)
            {
                if (--_writers == 0 && _disposed) _semaphore.Dispose();
            }
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly Action _release;
            internal Scope(Action release) => _release = release;
            public void Dispose() => _release();
        }
    }
}
