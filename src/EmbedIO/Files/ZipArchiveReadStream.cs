using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Files
{
    // One gate protects every archive operation, including reads after OpenFile returns.
    // References keep the gate alive through queued operations and open entry streams.
    internal sealed class ZipArchiveReadGate : IDisposable
    {
        private readonly object _lifetime = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly CancellationTokenSource _archiveStopped = new();
        private int _archiveStopSignaled;
        private int _references = 1;
        private int _ownerReleased;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
            { StopArchiveOperations(); Release(); }
        }

        internal void StopArchiveOperations()
        {
            if (Interlocked.Exchange(ref _archiveStopSignaled, 1) == 0) _archiveStopped.Cancel();
        }

        internal Scope EnterArchive()
        {
            Retain();
            try
            {
                // Task-based waiting lets modern runtimes compensate blocked pool workers.
                // A synchronous semaphore wait can starve the async read holding this gate.
                _semaphore.WaitAsync(_archiveStopped.Token).GetAwaiter().GetResult();
                return new Scope(this);
            }
            catch (OperationCanceledException)
            {
                Release();
                throw new ObjectDisposedException("ZipArchive");
            }
            catch { Release(); throw; }
        }

        internal void Retain()
        {
            lock (_lifetime)
            {
                if (_references == 0) throw new ObjectDisposedException("ZipArchive");
                _references++;
            }
        }

        internal void Release()
        {
            bool dispose;
            lock (_lifetime) { dispose = --_references == 0; }
            if (dispose) { _semaphore.Dispose(); _archiveStopped.Dispose(); }
        }

        internal Scope Enter()
        {
            Retain();
            try { _semaphore.WaitAsync().GetAwaiter().GetResult(); return new Scope(this); }
            catch { Release(); throw; }
        }

        internal async Task<Scope> EnterAsync(CancellationToken token)
        {
            Retain();
            try
            {
                await _semaphore.WaitAsync(token).ConfigureAwait(false);
                return new Scope(this);
            }
            catch { Release(); throw; }
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly EmbedIO.Internal.BorrowedResource<ZipArchiveReadGate> _gateReference;
            private ZipArchiveReadGate _gate => _gateReference.Value;
            internal Scope(ZipArchiveReadGate gate) => _gateReference = new EmbedIO.Internal.BorrowedResource<ZipArchiveReadGate>(gate);
            public void Dispose() { _gate._semaphore.Release(); _gate.Release(); }
        }
    }

    internal sealed class ZipArchiveReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly EmbedIO.Internal.BorrowedResource<ZipArchiveReadGate> _gateReference;
        private ZipArchiveReadGate _gate => _gateReference.Value;
        private int _disposed;
        private int _closed;

        internal ZipArchiveReadStream(Stream inner, ZipArchiveReadGate gate)
        {
            _inner = inner;
            _gateReference = new EmbedIO.Internal.BorrowedResource<ZipArchiveReadGate>(gate);
            gate.Retain();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanTimeout => _inner.CanTimeout;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }
        public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
        public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Volatile.Read(ref _closed) != 0) return _inner.Read(buffer, offset, count);
            using var scope = _gate.Enter();
            return _inner.Read(buffer, offset, count);
        }

        public override int ReadByte()
        {
            if (Volatile.Read(ref _closed) != 0) return _inner.ReadByte();
            using var scope = _gate.Enter();
            return _inner.ReadByte();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _closed) != 0) return _inner.ReadAsync(buffer, offset, count, cancellationToken);
            if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                // Let the actual runtime retain its argument types and validation order.
                using var scope = _gate.Enter();
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                return _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }
            return ReadCoreAsync(buffer, offset, count, cancellationToken);
        }

        private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var scope = await _gate.EnterAsync(token).ConfigureAwait(false);
            return await _inner.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
        }

#if NET10_0
        public override int Read(Span<byte> buffer)
        {
            if (Volatile.Read(ref _closed) != 0) return _inner.Read(buffer);
            using var scope = _gate.Enter();
            return _inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Volatile.Read(ref _closed) != 0 ? _inner.ReadAsync(buffer, cancellationToken) : ReadMemoryCoreAsync(buffer, cancellationToken);

        private async ValueTask<int> ReadMemoryCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using var scope = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                using var scope = await _gate.EnterAsync(CancellationToken.None).ConfigureAwait(false);
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally { Volatile.Write(ref _closed, 1); _gate.Release(); GC.SuppressFinalize(this); }
        }
#endif

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);
#if NET10_0
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);
#endif
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { using var scope = _gate.Enter(); _inner.Dispose(); }
                finally { Volatile.Write(ref _closed, 1); _gate.Release(); }
            }
            base.Dispose(disposing);
        }

    }
}
