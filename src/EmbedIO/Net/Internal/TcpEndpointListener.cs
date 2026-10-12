using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // Routing belongs to EndpointRoutes, accepted sessions to admission, and
    // protocol negotiation to the session. This coordinator transfers ownership
    // between those components without running TLS or handlers in the accept loop.
    internal sealed class EndPointListener : IDisposable
    {
        private readonly EndpointRoutes _routes = new();
        private readonly TcpEndpointAdmission _admission;
        private readonly IPEndPoint _endpoint;
        private Task _acceptWorker = Task.CompletedTask;
        private int _started;

        public EndPointListener(HttpListener listener, IPAddress address, int port, bool secure)
            : this(listener, address, port, secure, true) { }

        internal EndPointListener(HttpListener listener, IPAddress address, int port, bool secure, bool start)
        {
            Listener = listener;
            Secure = secure;
            _endpoint = new IPEndPoint(address, port);
            _admission = TcpEndpointAdmission.Bind(address, port);
            if (start) StartAccepting();
        }

        internal void StartAccepting()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0 || AdmissionStopped) return;
            try
            {
                if (_endpoint.AddressFamily == AddressFamily.InterNetworkV6
                    && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    _acceptWorker = Task.Factory.StartNew(AcceptBlocking, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                else
                {
                    var actor = new TcpAcceptLoop(_admission.Socket, ProcessAcceptedSocket,
                        () => AdmissionStopped, _admission.Stop);
                    _acceptWorker = Task.Run(actor.RunAsync);
                }
                _ = _acceptWorker.ContinueWith(completed =>
                {
                    if (completed.Exception == null) return;
                    _admission.Stop();
                    "TCP endpoint accept worker failed.".Warn();
                }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            catch { _admission.Stop(); throw; }
        }

        internal HttpListener Listener { get; }
        internal IPEndPoint LocalEndpoint => _endpoint;
        internal bool RoutesEmpty => _routes.IsEmpty;
        internal bool Secure { get; }
        internal bool AdmissionStopped => _admission.IsStopped;
        internal Socket ListeningSocket => _admission.Socket;
        internal HttpConnection[] PendingConnections() => _admission.PendingSnapshot();
        internal bool TryPendingConnections(out HttpConnection[] connections) => _admission.TryPendingSnapshot(out connections);

        public bool BindContext(HttpListenerContext context)
        {
            var owner = SearchListener(context.Request.Url, out var prefix);
            if (owner == null) return false;
            context.Listener = owner;
            context.Connection.Prefix = prefix;
            return true;
        }

        public void UnbindContext(HttpListenerContext context) => context.Listener?.UnregisterContext(context);
        internal bool IsExclusiveTo(HttpListener owner) => _routes.IsExclusiveTo(owner);
        internal void StopAcceptingIfExclusive(HttpListener owner)
        {
            if (_routes.IsExclusiveTo(owner)) _admission.Stop();
        }
        internal HttpConnection[] StopAcceptingForDrain() => _admission.StopAndSnapshot();
        internal bool RetainIdleConnection(HttpConnection connection) => _admission.RetainIdle(connection);
        internal void RemoveConnection(HttpConnection connection) => _admission.Release(connection);

        public void Dispose()
        {
            var pending = _admission.CloseAndSnapshot(out var failure);
            foreach (var connection in pending)
            {
                try { connection.Dispose(); }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    // A failed session must not prevent release of the others.
                    // Preserve the first failure's type and original location.
                    failure ??= ExceptionDispatchInfo.Capture(error);
                }
            }
            failure?.Throw();
        }

        public bool AddPrefix(ListenerPrefix prefix, HttpListener listener)
        {
            if (AdmissionStopped) throw new HttpListenerException(995, "The endpoint stopped accepting connections.");
            return _routes.Add(prefix, listener);
        }

        internal void DetachRoute(ListenerPrefix prefix, HttpListener listener) => _routes.Remove(prefix, listener);

        internal HttpListener? SearchListener(Uri uri, out ListenerPrefix? prefix) => _routes.Find(uri, out prefix);

        private void ProcessAcceptedSocket(Socket peer)
        {
            HttpConnection? session = null;
            var policy = Listener.ConnectionPolicy;
            var slot = false;
            try
            {
                if (Secure && Listener.Certificate == null) return;
                // Over MaximumConnections the peer is closed without a response; no TLS or
                // protocol work starts for it.
                if (!(slot = policy.TryAcquire())) return;
                session = _admission.CreatePending(peer, this, policy);
                if (session == null) return;
                try
                {
#if NET10_0_OR_GREATER
                    var queued = ThreadPool.QueueUserWorkItem(static connection => { _ = connection.BeginReadRequest(); }, session, false);
#else
                    var queued = ThreadPool.QueueUserWorkItem(static state =>
                    {
                        if (state is HttpConnection connection) _ = connection.BeginReadRequest();
                    }, session);
#endif
                    if (!queued) session.Dispose();
                }
                catch { session.Dispose(); throw; }
            }
            catch (Exception error) when (session == null && ExceptionPolicy.IsRecoverable(error))
            { /* No session acquired this peer; the finally releases it. */ }
            finally
            {
                // A created session owns its slot and releases it when it retires.
                if (session == null)
                {
                    if (slot) policy.Release();
                    peer.Dispose();
                }
            }
        }

        private void AcceptBlocking()
        {
            // Retain the validated macOS IPv6 workaround: public BCL async accept
            // can fail before its continuation when constructing a peer endpoint.
            var reportedAddress = false;
            while (!AdmissionStopped)
            {
                Socket peer;
                try { peer = _admission.Socket.Accept(); }
                catch (Exception error) when (error is ObjectDisposedException or SocketException
                    or ArgumentException { ParamName: "socketAddress" })
                {
                    if (error is ObjectDisposedException || AdmissionStopped) return;
                    if (error is ArgumentException)
                    {
                        if (!reportedAddress)
                        {
                            reportedAddress = true;
                            "Discarded an accepted socket with an invalid peer address on macOS.".Warn();
                        }
                    }
                    else Thread.Sleep(100);
                    continue;
                }
                ProcessAcceptedSocket(peer);
            }
        }
    }
}
