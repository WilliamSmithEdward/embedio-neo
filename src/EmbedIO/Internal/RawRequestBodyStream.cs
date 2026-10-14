using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;

namespace EmbedIO.Internal
{
    // The application view borrows framing input, whose existing reader owns
    // native cancellation storage. The private EOF probe is never pooled or
    // exposed as successful application data.
    internal sealed class RawRequestBodyStream : Stream
    {
        private readonly BorrowedResource<Stream> _source;
        private readonly long _maximum;
        private readonly byte[] _probe = new byte[1];
        private long _consumed;
        private int _reading;
        private int _disposed;
        private int _retired;
        private bool _complete;
        private RawRequestBodyLimitException? _failure;
        private readonly RawRequestBodyLimitException? _knownRefusal;
        private readonly IRequestTrailerSource? _physicalInput;
        private int _trailerInputAbandoned;

        internal RawRequestBodyStream(Stream source, long maximum)
            : this(source, maximum, null) { }

        internal RawRequestBodyStream(Stream source, long maximum, RawRequestBodyLimitException? knownRefusal)
            : this(source, maximum, knownRefusal, null) { }

        internal RawRequestBodyStream(Stream source, long maximum, RawRequestBodyLimitException? knownRefusal,
            IRequestTrailerSource? physicalInput)
        {
            if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
            _source = new BorrowedResource<Stream>(source ?? throw new ArgumentNullException(nameof(source)));
            _maximum = maximum;
            _knownRefusal = knownRefusal;
            _physicalInput = physicalInput;
        }

        internal RawRequestBodyLimitException? LimitFailure => Volatile.Read(ref _failure);
        internal bool HasLimitFailure => LimitFailure != null;
        internal bool ReadInProgress => Volatile.Read(ref _reading) != 0;
        internal bool TrailerInputAbandoned => Volatile.Read(ref _trailerInputAbandoned) != 0;
        // True once a read reached the validated end and nothing has happened since: completing
        // the input would read nothing more and could not fail.
        internal bool IsValidatedEnd => Volatile.Read(ref _complete) && Volatile.Read(ref _reading) == 0
            && Volatile.Read(ref _retired) == 0 && LimitFailure == null;
        // Like the other body wrappers, the view reports a source that stopped reading on its
        // own, as an HTTP/3 body does once a canceled read abandons its input.
        public override bool CanRead => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _retired) == 0
            && _source.Value.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        private static void Validate(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(offset));
        }

        private void Check(bool cleanup)
        {
            if (Volatile.Read(ref _retired) != 0 || (!cleanup && Volatile.Read(ref _disposed) != 0))
                throw new ObjectDisposedException(nameof(RawRequestBodyStream));
            if (_failure != null) throw _failure;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count);
            Enter();
            try
            {
                Check(false);
                if (count == 0 || _complete) return 0;
                var remaining = _maximum - _consumed;
                if (remaining == 0) return Probe(_source.Value.Read(_probe, 0, 1));
                return Account(_source.Value.Read(buffer, offset, (int)Math.Min(remaining, count)));
            }
            finally { Volatile.Write(ref _reading, 0); }
        }

        private void Enter()
        {
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent request body reads.");
        }

        private int Probe(int read)
        {
            if (read != 0) throw _failure = _knownRefusal ?? new RawRequestBodyLimitException();
            _complete = true;
            return 0;
        }

        private int Account(int read)
        {
            _consumed += read;
            if (read == 0) _complete = true;
            return read;
        }

        public override int ReadByte()
        {
            Enter();
            try
            {
                Check(false);
                if (_complete) return -1;
                var read = _source.Value.Read(_probe, 0, 1);
                if (_consumed == _maximum) { Probe(read); return -1; }
                return Account(read) == 0 ? -1 : _probe[0];
            }
            finally { Volatile.Write(ref _reading, 0); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadCoreAsync(buffer, offset, count, cancellationToken, false);

        private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, CancellationToken token, bool cleanup)
        {
            Validate(buffer, offset, count);
            token.ThrowIfCancellationRequested();
            Enter();
            try
            {
                Check(cleanup);
                if (count == 0 || _complete) return 0;
                var remaining = _maximum - _consumed;
                if (remaining == 0)
                {
                    return Probe(await _source.Value.ReadAsync(_probe, 0, 1, token).ConfigureAwait(false));
                }
                var read = await _source.Value.ReadAsync(buffer, offset, (int)Math.Min(remaining, count), token).ConfigureAwait(false);
                return Account(read);
            }
            finally { Volatile.Write(ref _reading, 0); }
        }

#if NET10_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            Enter();
            try
            {
                Check(false);
                if (buffer.IsEmpty || _complete) return 0;
                var remaining = _maximum - _consumed;
                if (remaining == 0) return Probe(_source.Value.Read(_probe.AsSpan()));
                return Account(_source.Value.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]));
            }
            finally { Volatile.Write(ref _reading, 0); }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Enter();
            try
            {
                Check(false);
                if (buffer.IsEmpty || _complete) return 0;
                var remaining = _maximum - _consumed;
                if (remaining == 0)
                    return Probe(await _source.Value.ReadAsync(_probe.AsMemory(), cancellationToken).ConfigureAwait(false));
                return Account(await _source.Value.ReadAsync(buffer[..(int)Math.Min(remaining, buffer.Length)], cancellationToken).ConfigureAwait(false));
            }
            finally { Volatile.Write(ref _reading, 0); }
        }
#endif

        // Includes disposed public views. Disposal cannot reset accounting or
        // make unread-input cleanup bypass the configured entity-byte ceiling.
        internal async Task CompleteInputAsync(CancellationToken token)
        {
            const int length = 2048;
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            while (await ReadCoreAsync(buffer, 0, length, token, true).ConfigureAwait(false) != 0) { }
            // Every read above has settled. A failed drain leaves the buffer to the collector
            // rather than return storage a source might still be writing.
            ArrayPool<byte>.Shared.Return(buffer, true);
        }

        internal void Retire()
        {
            Interlocked.Exchange(ref _retired, 1);
        }

        public override void Flush() => Check(false);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            // Physical framing can finish on a positive read. Preserve that
            // snapshot, but do not let cleanup or late FIN undo abandonment.
            if (disposing)
            {
                if (_physicalInput != null && !_physicalInput.TryGetTrailers(out _))
                    Interlocked.Exchange(ref _trailerInputAbandoned, 1);
                Interlocked.Exchange(ref _disposed, 1);
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class RawRequestBodyLimitException : HttpException
    {
        internal RawRequestBodyLimitException() : base(413, LimitedRequestStream.TransferredLimitMessage) { }
    }
}
