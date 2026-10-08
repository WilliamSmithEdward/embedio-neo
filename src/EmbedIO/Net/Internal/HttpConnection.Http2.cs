using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal
{
    internal sealed partial class HttpConnection
    {
        private async Task RunHttp2Async(Stream transport)
        {
            using var stop = new CancellationTokenSource();
            lock (_connectionSync)
            {
                if (_sock == null || _resourcesDisposed != 0) return;
                _http2 = true;
                _http2Stop = new EmbedIO.Internal.BorrowedResource<CancellationTokenSource>(stop);
                _http2Listeners = new HashSet<HttpListener>();
            }
            try
            {
                using var connection = await Http2Connection.AcceptAsync(transport, stop.Token).ConfigureAwait(false);
                StopRequestTimer();
                using var dispatcher = new Http2Dispatcher(connection);
                await dispatcher.RunAsync(DispatchHttp2Async, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                CloseTransport(true);
                lock (_connectionSync) _http2Stop = null;
            }
        }

        private async Task DispatchHttp2Async(Http2Exchange exchange)
        {
            var context = new Http2Context(exchange, LocalEndPoint, RemoteEndPoint, IsSecure);
            HttpListener? listener = null;
            try
            {
                listener = _epl.SearchListener(context.Request.Url, out _);
                if (listener == null)
                {
                    context.Response.StatusCode = 404;
                    context.Close();
                    return;
                }
                lock (_connectionSync)
                {
                    if (_sock == null || _resourcesDisposed != 0) throw new IOException("Connection closed before routing.");
                    (_http2Listeners ?? throw new InvalidOperationException("HTTP/2 routing has not started.")).Add(listener);
                }
                // Do not hold a connection lock while entering listener lifecycle
                // synchronization: listener shutdown closes its connections.
                listener.RegisterMultiplexedContext(context, this);
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
                listener?.UnregisterContext(context);
                // Cleanup runs on application dispatch, never synchronously inside
                // a cancellation callback holding a listener/connection lock.
                try { context.Close(); }
                catch (Exception error) when (exchange.CancellationToken.IsCancellationRequested && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { }
                if (context.Completion.IsFaulted) _ = context.Completion.Exception;
            }
        }
    }
}
