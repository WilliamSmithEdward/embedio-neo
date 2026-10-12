using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Bounds how long one read of a request body may wait for data. A read that misses the
    // deadline is abandoned: the stream fails with HTTP 408 from then on, and the transport
    // that owns the pending read retires it when the request ends.
    internal sealed class ReadTimeoutStream : Stream
    {
        internal const string TimeoutMessage = "The request body was not received within the configured time.";
        private readonly Stream _source;
        private readonly TimeSpan _timeout;
        private Action? _onTimeout;
        private int _timedOut;

        internal ReadTimeoutStream(Stream source, TimeSpan timeout, Action? onTimeout)
        {
            _source = source;
            _timeout = timeout;
            _onTimeout = onTimeout;
        }

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        private HttpException Expired()
        {
            if (Interlocked.Exchange(ref _timedOut, 1) == 0) Interlocked.Exchange(ref _onTimeout, null)?.Invoke();
            return new HttpException(408, TimeoutMessage);
        }

        private void ThrowIfExpired()
        {
            if (Volatile.Read(ref _timedOut) != 0) throw new HttpException(408, TimeoutMessage);
        }

        // The abandoned read is left to the transport; observe its outcome so a later
        // fault is not reported as unobserved.
        private static void Abandon(Task read)
            => _ = read.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfExpired();
            var read = _source.ReadAsync(buffer, offset, count, CancellationToken.None);
            bool completed;
            try { completed = read.Wait(_timeout); }
            catch (AggregateException) { completed = true; }
            if (!completed) { Abandon(read); throw Expired(); }
            return read.GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfExpired();
            return await WithDeadlineAsync(_source.ReadAsync(buffer, offset, count, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        private async Task<int> WithDeadlineAsync(Task<int> read, CancellationToken cancellationToken)
        {
            if (read.IsCompleted) return await read.ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            var expiry = Task.Delay(Timeout.Infinite, deadline.Token);
            if (await Task.WhenAny(read, expiry).ConfigureAwait(false) == read) return await read.ConfigureAwait(false);
            Abandon(read);
            cancellationToken.ThrowIfCancellationRequested();
            throw Expired();
        }
#if NET10_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            var array = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var count = Read(array, 0, buffer.Length);
                array.AsSpan(0, count).CopyTo(buffer);
                return count;
            }
            finally { ArrayPool<byte>.Shared.Return(array, true); }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfExpired();
            var read = _source.ReadAsync(buffer, cancellationToken);
            return read.IsCompletedSuccessfully ? read : new ValueTask<int>(WithDeadlineAsync(read.AsTask(), cancellationToken));
        }
#endif
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
