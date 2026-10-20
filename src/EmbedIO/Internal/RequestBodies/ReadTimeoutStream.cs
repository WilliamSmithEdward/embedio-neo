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
        private readonly TaskCompletionSource<bool> _retirement = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private Action? _onTimeout;
        private HttpException? _timeoutFailure;
        private int _readPending;

        internal ReadTimeoutStream(Stream source, TimeSpan timeout, Action? onTimeout)
        {
            _source = source;
            _timeout = timeout;
            _onTimeout = onTimeout;
        }

        public override bool CanRead => !_retirement.Task.IsCompleted && _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private HttpException Expired()
        {
            var failure = new HttpException(408, TimeoutMessage);
            var previous = Interlocked.CompareExchange(ref _timeoutFailure, failure, null);

            if (previous != null) return previous;

            Interlocked
                .Exchange(ref _onTimeout, null)
                ?.Invoke();
            return failure;
        }

        internal bool IsTimeoutFailure(Exception failure) =>
            ReferenceEquals(Volatile.Read(ref _timeoutFailure), failure);

        // True while a read is in flight, including one the deadline abandoned that the
        // source has not completed yet: the source must not be read by anyone else.
        internal bool HasUnsettledRead => Volatile.Read(ref _readPending) != 0;

        // True once a read missed the deadline; every later read reports that failure.
        internal bool HasExpired => Volatile.Read(ref _timeoutFailure) != null;

        // The request owner retires this borrowed view; it still owns disposal of the transport.
        internal void Retire()
        {
            Interlocked.Exchange(ref _onTimeout, null);
            _retirement.TrySetResult(true);
        }

        private void ThrowIfUnavailable()
        {
            if (_retirement.Task.IsCompleted) throw new ObjectDisposedException(nameof(ReadTimeoutStream));

            var failure = Volatile.Read(ref _timeoutFailure);

            if (failure != null) throw failure;
        }

        private void ReleaseStorage(byte[]? buffer)
        {
            if (buffer == null) return;

            ArrayPool<byte>.Shared.Return(buffer, true);
            Volatile.Write(ref _readPending, 0);
        }

        private async Task<ReadResult> ReadOwnedAsync(int count, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();

            if (count == 0) return default;

            if (Interlocked.CompareExchange(ref _readPending, 1, 0) != 0)
                throw new InvalidOperationException("The previous request body read has not settled.");

            byte[]? buffer = null;
            Task<int>? read = null;
            var transferred = false;

            try
            {
                var length = Math.Min(count, 64 * 1024);
                buffer = ArrayPool<byte>.Shared.Rent(length);
#if NET10_0_OR_GREATER
                read = _source
                    .ReadAsync(buffer.AsMemory(0, length), cancellationToken)
                    .AsTask();
#else
                read = _source.ReadAsync(buffer, 0, length, cancellationToken);
#endif
                var received = await WithDeadlineAsync(read, cancellationToken).ConfigureAwait(false);

                if (received < 0 || received > length)
                    throw new IOException("The request body source returned an invalid read count.");

                transferred = true;
                return new ReadResult(buffer, received);
            }

            finally
            {
                if (!transferred)
                {
                    if (buffer == null) Volatile.Write(ref _readPending, 0);

                    else if (read == null || read.IsCompleted)
                    {
                        // A read can fault after a deadline, cancellation or retirement already won,
                        // so nothing awaited it. Observe its exception here, or it surfaces later as
                        // an unobserved task exception. The storage is free either way.
                        _ = read?.Exception;
                        ReleaseStorage(buffer);
                    }

                    else
                    {
                        // A source may ignore cancellation. Keep its private storage until its
                        // actual task settles, rather than returning a still-owned pooled buffer.
                        _ = read.ContinueWith(
                            static (task, state) =>
                            {
                                var lease = ((ReadTimeoutStream Owner, byte[] Buffer))(
                                    state
                                        ?? throw new InvalidOperationException(
                                            "Missing pending read storage ownership."
                                        )
                                );
                                _ = task.Exception;
                                lease.Owner.ReleaseStorage(lease.Buffer);
                            },
                            (this, buffer),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default
                        );
                    }
                }
            }
        }

        private readonly struct ReadResult
        {
            internal ReadResult(byte[] buffer, int count)
            {
                Buffer = buffer;
                Count = count;
            }

            internal byte[]? Buffer { get; }
            internal int Count { get; }
        }

        private static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));

            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            if (offset > buffer.Length - count) throw new ArgumentException("The read exceeds the supplied buffer.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            var result = ReadOwnedAsync(count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            try
            {
                if (result.Buffer != null) Array.Copy(result.Buffer, 0, buffer, offset, result.Count);

                return result.Count;
            }

            finally
            {
                ReleaseStorage(result.Buffer);
            }
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            ValidateBuffer(buffer, offset, count);
            var result = await ReadOwnedAsync(count, cancellationToken).ConfigureAwait(false);

            try
            {
                if (result.Buffer != null) Array.Copy(result.Buffer, 0, buffer, offset, result.Count);

                return result.Count;
            }

            finally
            {
                ReleaseStorage(result.Buffer);
            }
        }

        private async Task<int> WithDeadlineAsync(Task<int> read, CancellationToken cancellationToken)
        {
            if (read.IsCompleted) return await read.ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            var expiry = Task.Delay(Timeout.Infinite, deadline.Token);

            if (
                await Task
                    .WhenAny(read, expiry, _retirement.Task)
                    .ConfigureAwait(false)
                == read
            ) return await read.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
            throw Expired();
        }
#if NET10_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            var result = ReadOwnedAsync(buffer.Length, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            try
            {
                result.Buffer
                    ?.AsSpan(0, result.Count)
                    .CopyTo(buffer);

                return result.Count;
            }

            finally
            {
                ReleaseStorage(result.Buffer);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var result = await ReadOwnedAsync(buffer.Length, cancellationToken).ConfigureAwait(false);

            try
            {
                result.Buffer
                    ?.AsMemory(0, result.Count)
                    .CopyTo(buffer);

                return result.Count;
            }

            finally
            {
                ReleaseStorage(result.Buffer);
            }
        }
#endif
        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Retire();
                _source.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
