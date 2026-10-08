using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal
{
    internal sealed partial class HttpConnection
    {
        // The caller must stop this owner's admission before taking the snapshot.
        // Shared HTTP/2 streams cannot use connection-wide GOAWAY or socket drain.
        internal Task DrainForListenerAsync(HttpListener owner)
        {
            lock (_connectionSync)
            {
                if (_http2 && !_epl.AdmissionStopped)
                {
                    if (_http2Listeners == null || !_http2Listeners.TryGetValue(owner, out var exchanges))
                        return Task.CompletedTask;
                    return WaitForOwnerContextsAsync(exchanges.Values.Select(context => context.Completion).ToArray());
                }
            }
            return DrainCoreAsync(owner);
        }

        private static async Task WaitForOwnerContextsAsync(Task[] completions)
        {
            try { await Task.WhenAll(completions).ConfigureAwait(false); }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                // A reset/aborted response is terminal too. Its failure is already
                // observed by request dispatch; drain only waits for owned cleanup.
            }
        }

        internal void CloseForListener(HttpListener owner)
        {
            List<Http2Exchange>? canceled = null;
            lock (_connectionSync)
            {
                // A snapshot can outlive an HTTP/1 keep-alive ownership transfer.
                // Decide ownership and block a later transfer under the same lock.
                if (!_http2)
                {
                    if (!_epl.AdmissionStopped && _lastListener != owner) return;
                    Volatile.Write(ref _forceClosing, 1);
                }
                // A shared endpoint remains open after this owner removes its routes.
                // Other owners can still have streams on this same HTTP/2 connection.
                if (_http2 && !_epl.AdmissionStopped)
                {
                    canceled = new List<Http2Exchange>();
                    if (_http2Listeners != null && _http2Listeners.TryGetValue(owner, out var exchanges))
                    {
                        canceled.AddRange(exchanges.Keys);
                        _http2Listeners.Remove(owner);
                    }
                }
            }
            if (canceled == null) { ForceClose(); return; }
            foreach (var exchange in canceled)
            {
                try { exchange.Cancel(new IOException("The owning listener stopped.")); }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                {
                    // An exchange can finish while Stop snapshots its owner. One
                    // callback failure must not prevent cancellation of other streams.
                    if (error is not ObjectDisposedException) error.Log("HTTP/2 listener shutdown");
                }
            }
        }

        private async Task RunHttp2Async(Stream transport)
        {
            using var stop = new CancellationTokenSource();
            lock (_connectionSync)
            {
                if (_sock == null || _resourcesDisposed != 0) return;
                _http2 = true;
                _http2Stop = new EmbedIO.Internal.BorrowedResource<CancellationTokenSource>(stop);
                _http2Listeners = new Dictionary<HttpListener, Dictionary<Http2Exchange, MultiplexedContext>>();
            }
            try
            {
                using var connection = await Http2Connection.AcceptAsync(transport, stop.Token).ConfigureAwait(false);
                StopRequestTimer();
                using var dispatcher = new Http2Dispatcher(connection);
                lock (_connectionSync)
                {
                    if (_sock == null || _resourcesDisposed != 0) return;
                    _http2Dispatcher = new EmbedIO.Internal.BorrowedResource<Http2Dispatcher>(dispatcher);
                }
                try { await dispatcher.RunAsync(DispatchHttp2Async, stop.Token).ConfigureAwait(false); }
                finally { lock (_connectionSync) _http2Dispatcher = null; }
            }
            finally
            {
                CloseTransport(true);
                lock (_connectionSync) _http2Stop = null;
            }
        }

        private async Task DispatchHttp2Async(Http2Exchange exchange)
        {
            var context = new MultiplexedContext(exchange, LocalEndPoint, RemoteEndPoint, IsSecure);
            HttpListener? listener = null;
            var registered = false;
            try
            {
                listener = _epl.SearchListener(context.Request.Url, out _);
                if (listener == null)
                {
                    if (_epl.AdmissionStopped) throw new IOException("Endpoint stopped before request routing.");
                    context.Response.StatusCode = 404;
                    await context.CloseAsync().ConfigureAwait(false);
                    return;
                }
                lock (_connectionSync)
                {
                    if (_sock == null || _resourcesDisposed != 0) throw new IOException("Connection closed before routing.");
                    var owners = _http2Listeners ?? throw new InvalidOperationException("HTTP/2 routing has not started.");
                    if (!owners.TryGetValue(listener, out var exchanges))
                        owners.Add(listener, exchanges = new Dictionary<Http2Exchange, MultiplexedContext>());
                    exchanges.Add(exchange, context);
                }
                // Do not hold a connection lock while entering listener lifecycle
                // synchronization: listener shutdown closes its connections.
                listener.RegisterMultiplexedContext(context, this);
                registered = true;
                var closed = false;
                lock (_connectionSync) closed = _sock == null || _resourcesDisposed != 0;
                if (closed)
                {
                    listener.RemoveConnection(this);
                    throw new IOException("Connection closed during routing.");
                }
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = exchange.CancellationToken.Register(state => ((TaskCompletionSource<bool>)(state ?? throw new InvalidOperationException("Missing cancellation signal."))).TrySetResult(true), canceled);
                var completed = await Task.WhenAny(context.Completion, canceled.Task).ConfigureAwait(false);
                if (completed == canceled.Task) exchange.CancellationToken.ThrowIfCancellationRequested();
                await context.Completion.ConfigureAwait(false);
            }
            finally
            {
                // Cleanup runs on application dispatch, never synchronously inside
                // a cancellation callback holding a listener/connection lock.
                try
                {
                    if (registered) await context.CloseAsync().ConfigureAwait(false);
                    else await context.AbortAsync().ConfigureAwait(false);
                }
                catch (Exception error) when ((!registered || context.CancellationToken.IsCancellationRequested)
                    && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                { }
                finally
                {
                    // Keep reset responses visible to a concurrent owner drain until
                    // their response and close callbacks have actually finished.
                    lock (_connectionSync)
                        if (listener != null && _http2Listeners != null && _http2Listeners.TryGetValue(listener, out var exchanges))
                            exchanges.Remove(exchange);
                    listener?.UnregisterContext(context);
                    if (context.Completion.IsFaulted) _ = context.Completion.Exception;
                }
            }
        }
    }
}
