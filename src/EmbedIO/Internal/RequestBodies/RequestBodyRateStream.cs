using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Checks every monitored request body once per second on one timer shared by the
    // process, as Kestrel's Heartbeat does. The timer runs only while a body is monitored.
    // Bodies are kept in a list with their slot index, so a body leaves in constant time.
    internal static class RequestBodyRateHeartbeat
    {
        internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
        private static readonly object Sync = new();
        private static readonly List<RequestBodyRateStream> Monitored = [];
        private static TimeSpan _interval = DefaultInterval;
        private static Timer? _timer;
        private static int _ticking;

        // The check interval. Tests shorten it through reflection while nothing is
        // monitored; the timer takes it when the first body is added.
        internal static TimeSpan Interval
        {
            get => _interval;
            set
            {
                if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(value));

                _interval = value;
            }
        }

        // Read by tests through reflection.
        internal static int MonitoredCount
        {
            get
            {
                lock (Sync) return Monitored.Count;
            }
        }

        internal static void Add(RequestBodyRateStream body)
        {
            lock (Sync)
            {
                body.Slot = Monitored.Count;
                Monitored.Add(body);

                if (Monitored.Count == 1)
                    (_timer ??= new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite)).Change(_interval, _interval);
            }
        }

        internal static void Remove(RequestBodyRateStream body)
        {
            lock (Sync)
            {
                var slot = body.Slot;

                if (slot < 0) return;

                var last = Monitored[Monitored.Count - 1];
                Monitored[slot] = last;
                last.Slot = slot;
                Monitored.RemoveAt(Monitored.Count - 1);
                body.Slot = -1;

                if (Monitored.Count == 0) _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private static void Tick(object? state)
        {
            if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;

            try
            {
                var now = Stopwatch.GetTimestamp();
                List<RequestBodyRateStream>? starved = null;

                lock (Sync)
                {
                    foreach (var body in Monitored)
                        if (body.IsStarved(now)) (starved ??= []).Add(body);
                }

                // Failing a body cancels its pending read; run that outside the lock.
                if (starved != null)
                    foreach (var body in starved) body.Fail();
            }

            finally
            {
                Volatile.Write(ref _ticking, 0);
            }
        }
    }

    // Measures the rate at which a request body arrives while the application waits for
    // it, and refuses the body once that rate stays below the minimum past the grace period.
    // Time the application spends not reading does not count. A read costs two timestamps;
    // the source is read with the body's own token so the heartbeat can end a pending read,
    // and the caller's token is linked to that token once per body, not per read. No buffer
    // is copied and no timer is created per read or per body.
    internal sealed class RequestBodyRateStream : Stream
    {
        internal const string FailureMessage = "The request body was not received at the configured minimum data rate.";
        private readonly Stream _source;
        private readonly double _minimumBytesPerTick;
        private readonly long _graceTicks;
        private readonly Func<bool>? _backPressured;
        private Action? _onFailure;
        private CancellationTokenSource? _readSource;
        private Link? _link;
        private HttpException? _failure;
        private long _bytes;
        private long _waitedTicks;
        private long _readStarted;
        private int _reading;
        private int _waiting;
        private int _retired;
        private int _suspended;
        private int _registered;

        // backPressured reports when the transport, not the client, holds the body back, as
        // an exhausted HTTP/2 connection window does; the rate is not enforced then.
        internal RequestBodyRateStream(
            Stream source,
            MinimumDataRate rate,
            Action? onFailure,
            Func<bool>? backPressured
        )
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));

            if (rate == null) throw new ArgumentNullException(nameof(rate));

            _minimumBytesPerTick = rate.BytesPerSecond / Stopwatch.Frequency;
            _graceTicks = (long)(rate.GracePeriod.TotalSeconds * Stopwatch.Frequency);
            _onFailure = onFailure;
            _backPressured = backPressured;
        }

        // The body's index in the heartbeat's list; owned by the heartbeat under its lock.
        internal int Slot = -1;

        // The caller's token linked to the body's source. Handlers pass the same token to
        // every read, so one registration serves the body; it is replaced when the token or
        // the source changes and dropped when the body ends, so nothing accumulates on a
        // long-lived server token.
        private sealed class Link
        {
            internal Link(
                CancellationTokenSource source,
                CancellationTokenRegistration registration,
                CancellationToken token
            )
            {
                Token = token;
                Source = source;
                Registration = registration;
            }

            internal CancellationToken Token { get; }
            internal CancellationTokenSource Source { get; }
            internal CancellationTokenRegistration Registration { get; }
        }

        private void Unlink() =>
            Interlocked
                .Exchange(ref _link, null)
                ?.Registration.Dispose();

        public override bool CanRead => Volatile.Read(ref _retired) == 0 && _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // True once the heartbeat refused the body; every later read reports that failure.
        internal bool HasFailed => Volatile.Read(ref _failure) != null;

        internal bool IsRateFailure(Exception error) => ReferenceEquals(Volatile.Read(ref _failure), error);

        // Called by the heartbeat under its lock. A snapshot torn by a read that completed
        // meanwhile is rejected rather than charged to the client.
        internal bool IsStarved(long now)
        {
            if (Volatile.Read(ref _waiting) == 0) return false;

            var started = Volatile.Read(ref _readStarted);
            var waited = Volatile.Read(ref _waitedTicks) + (now - started);
            var bytes = Volatile.Read(ref _bytes);

            if (Volatile.Read(ref _waiting) == 0 || Volatile.Read(ref _readStarted) != started) return false;

            if (waited <= _graceTicks) return false;

            if (_backPressured?.Invoke() == true) return false;

            return bytes < _minimumBytesPerTick * waited;
        }

        // Called by the heartbeat, outside its lock. The pending read, if any, is canceled;
        // the failure it then reports, and every later read reports, is the 408 recorded here.
        internal void Fail()
        {
            var failure = new HttpException(408, FailureMessage);

            if (Interlocked.CompareExchange(ref _failure, failure, null) != null) return;

            Volatile
                .Read(ref _readSource)
                ?.Cancel();
            Unregister();
            Unlink();
        }

        // The request owner retires this borrowed view when the request ends; a retired body
        // is no longer monitored. It still owns disposal of the transport.
        internal void Retire()
        {
            Interlocked.Exchange(ref _retired, 1);
            Unregister();
            Unlink();
        }

        // Stops monitoring while reads continue: the HTTP/1 discard of an unread body after
        // the response is bounded by the keep-alive deadline, as it was before the rate, and
        // Kestrel does not enforce its rate while draining either.
        internal void Suspend()
        {
            Interlocked.Exchange(ref _suspended, 1);
            Unregister();
        }

        private void Register()
        {
            if (Volatile.Read(ref _suspended) != 0) return;

            if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0) return;

            RequestBodyRateHeartbeat.Add(this);

            // A retirement or suspension that raced this registration found nothing to remove.
            if (Volatile.Read(ref _retired) != 0 || Volatile.Read(ref _suspended) != 0)
            {
                Unregister();
                // Unregister may have already changed the state before Add published its
                // slot. Remove is idempotent and now observes that published slot.
                RequestBodyRateHeartbeat.Remove(this);
            }
        }

        private void Unregister()
        {
            if (Interlocked.CompareExchange(ref _registered, 2, 1) == 1) RequestBodyRateHeartbeat.Remove(this);
        }

        // Runs the reuse callback once, on the reading thread, and returns the sticky failure.
        private HttpException Failed()
        {
            Interlocked
                .Exchange(ref _onFailure, null)
                ?.Invoke();
            return Volatile.Read(ref _failure)
                ?? throw new InvalidOperationException("Missing recorded data rate failure.");
        }

        private void ThrowIfUnavailable()
        {
            if (Volatile.Read(ref _retired) != 0) throw new ObjectDisposedException(nameof(RequestBodyRateStream));

            if (Volatile.Read(ref _failure) != null) throw Failed();
        }

        private CancellationTokenSource Enter(CancellationToken token)
        {
            ThrowIfUnavailable();

            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent request body reads.");

            try
            {
                // Detach an earlier caller before inspecting the reusable source. Disposal
                // waits for its cancellation callback, which may have canceled that source;
                // the check below then replaces it before the new caller begins reading.
                var previousLink = _link;

                if (previousLink != null && previousLink.Token != token) Unlink();

                // One source serves every read of the body until something cancels it: a
                // canceled caller token ends only that read, and the next read gets a new one.
                var source = _readSource;

                if (source == null || source.IsCancellationRequested)
                {
                    source = new CancellationTokenSource();
                    Interlocked.Exchange(ref _readSource, source);
                }

                if (token.CanBeCanceled)
                {
                    var link = _link;

                    if (link == null || link.Token != token || !ReferenceEquals(link.Source, source))
                    {
#if NET10_0_OR_GREATER
                        var registration = token.UnsafeRegister(
                            static state => ((CancellationTokenSource)(state
                                ?? throw new InvalidOperationException("Missing read cancellation source."))).Cancel(),
                            source
                        );
#else
                        var registration = token.Register(
                            static state => ((CancellationTokenSource)state).Cancel(),
                            source
                        );
#endif
                        Interlocked
                            .Exchange(ref _link, new Link(source, registration, token))
                            ?.Registration.Dispose();
                    }
                }

                Register();
                Volatile.Write(ref _readStarted, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _waiting, 1);

                // The heartbeat records the failure before it reads the source to cancel, and
                // this read published its source before checking: one of the two sees the other.
                if (Volatile.Read(ref _failure) != null) throw Failed();

                return source;
            }

            catch
            {
                Volatile.Write(ref _waiting, 0);
                Volatile.Write(ref _reading, 0);
                throw;
            }
        }

        private void Leave(int read)
        {
            var waited = Stopwatch.GetTimestamp() - _readStarted;
            Volatile.Write(ref _bytes, _bytes + read);
            Volatile.Write(ref _waitedTicks, _waitedTicks + waited);
            Volatile.Write(ref _waiting, 0);
            Volatile.Write(ref _reading, 0);
        }

        private static void Validate(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            Validate(buffer, offset, count);
            cancellationToken.ThrowIfCancellationRequested();

            if (count == 0)
            {
                ThrowIfUnavailable();
                return 0;
            }

            var source = Enter(cancellationToken);
            var read = 0;

            try
            {
                read = await _source
                    .ReadAsync(buffer, offset, count, source.Token)
                    .ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (
                Volatile.Read(ref _failure) == null && cancellationToken.IsCancellationRequested
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            catch (Exception) when (Volatile.Read(ref _failure) != null)
            {
                throw Failed();
            }

            finally
            {
                Leave(read);
            }

            return Account(read);
        }
#if NET10_0_OR_GREATER
        // The pooled builder keeps a read that completes asynchronously from boxing this
        // state machine, which is otherwise the only allocation a monitored read adds.
        [System.Runtime.CompilerServices.AsyncMethodBuilder(
            typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>)
        )]
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (buffer.IsEmpty)
            {
                ThrowIfUnavailable();
                return 0;
            }

            var source = Enter(cancellationToken);
            var read = 0;

            try
            {
                read = await _source
                    .ReadAsync(buffer, source.Token)
                    .ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (
                Volatile.Read(ref _failure) == null && cancellationToken.IsCancellationRequested
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            catch (Exception) when (Volatile.Read(ref _failure) != null)
            {
                throw Failed();
            }

            finally
            {
                Leave(read);
            }

            return Account(read);
        }
#endif
        // A read the heartbeat refused while it was completing reports the failure, not its
        // data: the connection or stream is ending anyway. The end of the body stops monitoring.
        private int Account(int read)
        {
            if (Volatile.Read(ref _failure) != null) throw Failed();

            if (read == 0)
            {
                Unregister();
                Unlink();
            }

            return read;
        }

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
