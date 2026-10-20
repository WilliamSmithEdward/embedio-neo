using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;

namespace EmbedIO.Testing.Internal
{
    internal sealed class TestContext : IHttpContextImpl, IHttpRequestAbortFeature, IDisposable
    {
        private readonly TimeKeeper _ageKeeper = new();
        private readonly Stack<Action<IHttpContext>> _closeCallbacks = new();
        private bool _closed;
        private RequestAbortToken? _requestAbort;

        internal TestContext(IHttpRequest request)
        {
            Request = request;
            User = Auth.NoUser;
            TestResponse = new TestResponse();
            Id = UniqueIdGenerator.GetNext();
            LocalEndPoint = Request.LocalEndPoint;
            RemoteEndPoint = Request.RemoteEndPoint;
            Route = RouteMatch.None;
            Session = SessionProxy.None;
        }

        public string Id { get; }
        public CancellationToken CancellationToken { get; set; }

        public CancellationToken GetRequestAbortedToken(bool cancelOnHttp1InputEnd = false)
        {
            lock (_closeCallbacks)
            {
                if (_requestAbort != null) return _requestAbort.Token;

                if (_closed) throw new InvalidOperationException("The HTTP context has already closed.");

                _requestAbort = new RequestAbortToken(
                    static error => Log.Source.TraceEvent(
                        System.Diagnostics.TraceEventType.Error,
                        0,
                        "HTTP context: request-abort callback failed: {0}",
                        error
                    ),
                    CancellationToken
                );
                return _requestAbort.Token;
            }
        }

        public void Dispose() => Close();

        public long Age => _ageKeeper.ElapsedTime;
        public IPEndPoint LocalEndPoint { get; }
        public IPEndPoint RemoteEndPoint { get; }
        public IHttpRequest Request { get; }
        public RouteMatch Route { get; set; }
        public string RequestedPath => Route.SubPath ?? string.Empty; // It will never be empty, because modules are matched via base routes - this is just to silence a warning.
        public IHttpResponse Response => TestResponse;
        internal TestResponse TestResponse { get; }
        public IPrincipal User { get; set; }
        public ISessionProxy Session { get; set; }
        public bool SupportCompressedRequests { get; set; }
        public IDictionary<object, object> Items { get; } = new Dictionary<object, object>();
        public bool IsHandled { get; set; }
        public MimeTypeProviderStack MimeTypeProviders { get; } = new MimeTypeProviderStack();

        public void SetHandled() => IsHandled = true;

        public void OnClose(Action<IHttpContext> callback)
        {
            lock (_closeCallbacks)
            {
                if (_closed)
                    throw new InvalidOperationException("HTTP context has already been closed.");

                _closeCallbacks.Push(Validate.NotNull(nameof(callback), callback));
            }
        }

        public Task<IWebSocketContext> AcceptWebSocketAsync(
            IEnumerable<string> requestedProtocols,
            string acceptedProtocol,
            int receiveBufferSize,
            TimeSpan keepAliveInterval,
            CancellationToken cancellationToken
        ) => throw new NotImplementedException("This HTTP context does not support the WebSocket protocol.");

        public void Close()
        {
            Action<IHttpContext>[] callbacks;

            lock (_closeCallbacks)
            {
                if (_closed) return;

                _closed = true;
                callbacks = [.. _closeCallbacks];
                _closeCallbacks.Clear();
            }

            _requestAbort?.Dispose();

            // Always close the response stream no matter what.
            Response.Close();

            foreach (var callback in callbacks)
            {
                try
                {
                    callback(this);
                }

                catch (Exception e) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(e))
                {
                    // A diagnostic listener must not prevent remaining close callbacks.
                    try
                    {
                        Log.Source.TraceEvent(
                            System.Diagnostics.TraceEventType.Error,
                            0,
                            "HTTP context: close callback failed: {0}",
                            e
                        );
                    }

                    catch (Exception diagnostic) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(diagnostic)) { }
                }
            }
        }

        public string? GetMimeType(string extension) => MimeTypeProviders.GetMimeType(extension);

        public bool TryDetermineCompression(string mimeType, out bool preferCompression) =>
            MimeTypeProviders.TryDetermineCompression(mimeType, out preferCompression);
    }
}
