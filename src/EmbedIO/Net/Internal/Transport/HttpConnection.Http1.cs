using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
#if NET10_0_OR_GREATER
using System.Threading.Tasks.Sources;
#endif
namespace EmbedIO.Net.Internal
{
    internal sealed partial class HttpConnection
#if NET10_0_OR_GREATER
    : IValueTaskSource<bool>
#endif
    {
        private bool _responseEnded;
        private bool _responseCanReuse;
        private bool _idleAwaitingSuccessor;
        private Task<int>? _idleRead;
#if NET10_0_OR_GREATER
        private ManualResetValueTaskSourceCore<bool> _responseCompletion;
        private bool _responseWaitRequested;
#else
        private TaskCompletionSource<bool>? _responseEndSignal;
#endif
        // Requests the actor serves between yields to the pool's global FIFO queue.
        // A socket completion resumes the actor on the completing thread, whose
        // local queue is LIFO, so with the handler now inline the newest
        // completions were served first and the oldest starved: p99 rose tenfold
        // at 64 connections while p50 fell sixfold. Kestrel dispatches every
        // receive completion to the pool for the same reason.
        private const int InlineRequestBudget = 1;

        // A single actor advances the HTTP/1 request cycle. Applications may
        // complete a response on another thread, but never start another reader.
        private async Task RunHttp1Async(int count, bool prefetched)
        {
            var inlineRequests = 0;

            while (Volatile.Read(ref _protocolEnded) == 0)
            {
                if (!await ReadAndAdmitHeadAsync(count, prefetched).ConfigureAwait(false)) return;

                // The next request waits for the response to end, not for the handler
                // to return: a handler may keep running after completing its response.
                // A directly dispatched handler that completed inline has already
                // ended it, so this wait then continues without suspending.
                if (!await WaitForResponseEndAsync().ConfigureAwait(false)) return;

                // Discarding a body the handler never read is the one step of the cycle
                // no deadline covered: the head deadline stopped once the head arrived and
                // the keep-alive deadline is armed by PrepareNextRequest, after this read.
                // A client that declares a body, takes its response and then stops sending
                // would hold the connection and its admission slot with no timer at all.
                if (!_context.HttpListenerRequest.IsBodyConsumed) ArmDrainDeadline();

                if (!await _context.HttpListenerRequest
                    .FlushInputAsync()
                    .ConfigureAwait(false))
                {
                    CloseTransport(true);
                    return;
                }

                if (!PrepareNextRequest())
                {
                    CloseTransport(true);
                    return;
                }

                prefetched = _pendingInput.Count != 0;
                count = prefetched ? 0 : await ReadHttp1InputAsync().ConfigureAwait(false);

                if (++inlineRequests >= InlineRequestBudget)
                {
                    inlineRequests = 0;
                    await Task.Yield();
                }
            }
        }

        private Task<int> ReadHttp1InputAsync()
        {
            lock (_connectionSync)
            {
                if (_protocolEnded != 0) return Task.FromResult(0);

                var reading = Stream.ReadAsync(
                    _buffer ?? throw new ObjectDisposedException(nameof(HttpConnection)),
                    0,
                    BufferSize
                );

                if (_idleAwaitingSuccessor) _idleRead = reading;

                return reading;
            }
        }

        private async Task<bool> ReadAndAdmitHeadAsync(int count, bool prefetched)
        {
            while (true)
            {
                ArraySegment<byte> input;
                bool complete;

                lock (_connectionSync)
                {
                    if (_protocolEnded != 0) return false;

                    input = prefetched
                        ? _pendingInput
                        : new ArraySegment<byte>(
                            _buffer ?? throw new ObjectDisposedException(nameof(HttpConnection)),
                            0,
                            count
                        );

                    if (input.Count != 0) _idleAwaitingSuccessor = false;

                    _idleRead = null;
                    complete = input.Count != 0 && ProcessInput(input);

                    if (complete && _errorMessage == null)
                    {
                        _context.HttpListenerRequest.FinishInitialization();

                        if (_errorMessage == null) StopRequestTimer();
                    }
                }

                if (input.Count == 0)
                {
                    CloseSocket();
                    return false;
                }

                if (!complete)
                {
                    prefetched = false;
                    count = await ReadHttp1InputAsync().ConfigureAwait(false);
                    continue;
                }

                if (_errorMessage != null)
                {
                    var reply = _headReader.ErrorStatusCode == 414
                        ? UriTooLongResponse
                        : _headReader.ErrorStatusCode == 431 ? HeaderFieldsTooLargeResponse : BadRequestResponse;
                    await RejectHeadAsync(reply).ConfigureAwait(false);
                    return false;
                }

                var routes = _epl.CaptureRoutes();

                if (!EndPointListener.BindContext(_context, routes))
                {
                    CloseTransport(true);
                    return false;
                }

                var owner = _context.Listener ?? throw new InvalidOperationException("Missing request owner.");
                owner.BodyPolicy?.Apply(_context);

                if (_context.HttpListenerRequest.RequiresContinue)
                {
                    // A server with body policies invites the upload only when a handler reads it.
                    if (owner.DeferContinue || _context.HttpListenerRequest.KnownBodyRefusal != null)
                        _context.HttpListenerRequest.DeferContinue(_context.SendDeferredContinueAsync);

                    else
                        await Stream
                            .WriteAsync(ContinueResponse, 0, ContinueResponse.Length)
                            .ConfigureAwait(false);
                }

                lock (_connectionSync)
                {
                    if (_protocolSocket == null || _protocolEnded != 0 || _forceClosing != 0 || _draining)
                        return false;

                    if (!ReferenceEquals(_lastListener, owner))
                    {
                        RemoveConnection();
                        owner.AddConnection(this);
                        _lastListener = owner;
                    }

                    _contextBound = true;
                }

                // With a dispatcher installed the handler has run on this thread up to
                // its first suspension; the server owns its task, including failures,
                // and every outcome ends the response that the actor waits for next.
                _ = owner.RegisterContext(_context);
                return true;
            }
        }

        private async Task RejectHeadAsync(byte[] reply)
        {
            try
            {
                // The lifetime timer bounds the write, send shutdown and raw
                // discard together, without extending an earlier head deadline.
                if (!_lifetime.ShortenDeadline(1000)) return;

                var discard = _buffer ?? throw new ObjectDisposedException(nameof(HttpConnection));
                await Stream
                    .WriteAsync(reply, 0, reply.Length)
                    .ConfigureAwait(false);
                await CompleteOutputAsync(false, CancellationToken.None).ConfigureAwait(false);

                // Never interpret or dispatch the rejected request's remaining
                // bytes. Staged close avoids a reset erasing its final response.
                while (
                    await Stream
                        .ReadAsync(discard, 0, discard.Length)
                        .ConfigureAwait(false)
                    != 0
                ) { }
            }

            finally
            {
                CloseTransport(true);
            }
        }
#if NET10_0_OR_GREATER
        private ValueTask<bool> WaitForResponseEndAsync()
#else
        private Task<bool> WaitForResponseEndAsync()
#endif
        {
            lock (_connectionSync)
            {
#if NET10_0_OR_GREATER
                if (_responseEnded) return new ValueTask<bool>(_responseCanReuse);

                _responseWaitRequested = true;
                return new ValueTask<bool>(this, _responseCompletion.Version);
#else
                if (_responseEnded) return Task.FromResult(_responseCanReuse);

                _responseEndSignal ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                return _responseEndSignal.Task;
#endif
            }
        }

        private void EndResponse(bool reuse)
        {
            lock (_connectionSync)
            {
#if NET10_0_OR_GREATER
                var notify = !_responseEnded;
#endif
                _responseEnded = true;
                _responseCanReuse = reuse
                    && _protocolSocket != null
                    && _protocolEnded == 0
                    && _forceClosing == 0
                    && !_draining;
#if NET10_0_OR_GREATER
                if (notify && _responseWaitRequested) _responseCompletion.SetResult(_responseCanReuse);
#else
                _responseEndSignal?.TrySetResult(_responseCanReuse);
#endif
            }
        }

        private void ResetResponseCompletion()
        {
            _responseEnded = false;
            _responseCanReuse = false;
#if NET10_0_OR_GREATER
            _responseWaitRequested = false;
            _responseCompletion.Reset();
            _responseCompletion.RunContinuationsAsynchronously = true;
#else
            _responseEndSignal = null;
#endif
        }
#if NET10_0_OR_GREATER
        bool IValueTaskSource<bool>.GetResult(short token) => _responseCompletion.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _responseCompletion.GetStatus(token);

        void IValueTaskSource<bool>.OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags
        ) => _responseCompletion.OnCompleted(continuation, state, token, flags);
#endif
        // Bounds the discard of an unread request body with the keep-alive budget: the
        // response is already sent, so a client that stops sending only costs itself reuse.
        private void ArmDrainDeadline()
        {
            lock (_connectionSync)
            {
                if (_protocolSocket == null || _protocolEnded != 0) return;

                _sTimeout = _keepAliveTimeout;
                _lifetime.ArmDeadline(_sTimeout);
            }
        }

        private bool PrepareNextRequest()
        {
            lock (_connectionSync)
            {
                if (_protocolSocket == null || _protocolEnded != 0 || _forceClosing != 0 || _draining) return false;

                var tail = _iStream?.BufferedRemainder ?? _pendingInput;
                Unbind();
                InitWithPendingInput(tail);
                _idleAwaitingSuccessor = tail.Count == 0;

                if (Reuses < int.MaxValue) Reuses++;

                _sTimeout = _keepAliveTimeout;
                _lifetime.ArmDeadline(_sTimeout);
                return true;
            }
        }
    }
}
