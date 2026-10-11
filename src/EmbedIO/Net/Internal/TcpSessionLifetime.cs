using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO.Net.Internal
{
    // Transport retirement has one claimant. Protocol actors relinquish their
    // borrowed resources before this owner publishes completed cleanup to drain.
    internal sealed class TcpSessionLifetime
    {
        private readonly object _gate;
        private readonly Socket _socket;
        private readonly Stream _stream;
        private int _state;
        private long _deadline;
        private bool _deadlineArmed;
        private ExceptionDispatchInfo? _failure;
        private TaskCompletionSource<bool>? _completion;

        internal TcpSessionLifetime(object gate, Socket socket, Stream stream, Action expired)
        {
            _gate = gate;
            _socket = socket;
            _stream = stream;
            DeadlineTimer = new Timer(InvokeExpiry, expired, Timeout.Infinite, Timeout.Infinite);
        }

        private static void InvokeExpiry(object? state)
        {
            if (state is not Action expired) return;
            try { expired(); }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                // Retirement has already recorded its failure for drain. A
                // callback has no synchronous caller to receive that exception.
                try { error.Log("TCP deadline cleanup"); }
                catch (Exception diagnosticError) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(diagnosticError))
                {
                    // A failing application trace observer cannot turn an
                    // already-observable teardown fault into an unhandled timer.
                }
            }
        }

        internal Timer DeadlineTimer { get; }
        internal bool CloseFinished => Volatile.Read(ref _state) == 2;

        internal Task Completion
        {
            get
            {
                lock (_gate)
                {
                    if (_state == 2) return _failure == null ? Task.CompletedTask : Task.FromException(_failure.SourceException);
                    _completion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _completion.Task;
                }
            }
        }

        internal void ArmDeadline(int milliseconds)
        {
            lock (_gate)
            {
                if (_state != 0) return;
                _deadline = Stopwatch.GetTimestamp() + (long)(milliseconds * (double)Stopwatch.Frequency / 1000);
                _deadlineArmed = true;
                DeadlineTimer.Change(milliseconds, Timeout.Infinite);
            }
        }

        internal void StopDeadline()
        {
            lock (_gate)
            {
                _deadlineArmed = false;
                if (_state == 0) DeadlineTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        internal bool TryRetire(bool deadlineOnly = false)
        {
            lock (_gate)
            {
                if (_state != 0) return false;
                // A timer callback already queued before Stop/Arm cannot retire
                // a request whose deadline was stopped or moved into the future.
                if (deadlineOnly)
                {
                    if (!_deadlineArmed) return false;
                    var remaining = _deadline - Stopwatch.GetTimestamp();
                    if (remaining > 0)
                    {
                        // Timer queues may wake before the monotonic deadline.
                        // Keep a one-shot expiry live instead of losing it.
                        var milliseconds = Math.Max(1, (int)Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency));
                        DeadlineTimer.Change(milliseconds, Timeout.Infinite);
                        return false;
                    }
                }
                _deadlineArmed = false;
                Volatile.Write(ref _state, 1);
                return true;
            }
        }

        // Called only by the successful retirement claimant, outside the gate.
        // Every owned resource is attempted even after a recoverable release fault.
        internal void FinishRetirement(bool shutdown, IDisposable? input, Action detachProtocol)
        {
            ExceptionDispatchInfo? first = null;
            try
            {
                if (shutdown)
                {
                    try { _socket.Shutdown(SocketShutdown.Both); }
                    catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
                }
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            { first = ExceptionDispatchInfo.Capture(error); }
            Release(_socket);
            Release(DeadlineTimer);
            Release(input);
            Release(_stream);
            try { detachProtocol(); }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            { first ??= ExceptionDispatchInfo.Capture(error); }
            lock (_gate)
            {
                _failure = first;
                Volatile.Write(ref _state, 2);
                if (first == null) _completion?.TrySetResult(true);
                else _completion?.TrySetException(first.SourceException);
            }
            first?.Throw();

            void Release(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                { first ??= ExceptionDispatchInfo.Capture(error); }
            }
        }
    }
}
