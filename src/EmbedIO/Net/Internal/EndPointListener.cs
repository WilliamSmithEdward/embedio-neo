using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO.Net.Internal
{
    internal sealed class EndPointListener : IDisposable
    {
        private readonly HashSet<HttpConnection> _unregistered;
        private readonly IPEndPoint _endpoint;
        private readonly Socket _sock;
        private readonly Task? _acceptWorker;
        private int _disposed;
        private int _acceptingStopped;
        private readonly EndpointRoutes _routes = new();

        public EndPointListener(HttpListener listener, IPAddress address, int port, bool secure)
        {
            Listener = listener;
            Secure = secure;
            _endpoint = new IPEndPoint(address, port);
            _sock = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            if (address.AddressFamily == AddressFamily.InterNetworkV6 && EndPointManager.UseIpv6)
            {
                _sock.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            }

            try
            {
                _sock.Bind(_endpoint);
                _sock.Listen(500);
            }
            catch
            {
                _sock.Dispose();
                throw;
            }
            _unregistered = new HashSet<HttpConnection>();
            if (address.AddressFamily == AddressFamily.InterNetworkV6
                && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS can throw during BCL async accept completion, before OnAccept.
                // A dedicated blocking accept keeps endpoint-construction errors catchable.
                _acceptWorker = Task.Factory.StartNew(AcceptOnWorker, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            else
            {
                var acceptLoop = new TcpAcceptLoop(_sock, ProcessAcceptedSocket, () => AdmissionStopped, StopAccepting);
                _acceptWorker = Task.Run(acceptLoop.RunAsync);
                _ = _acceptWorker.ContinueWith(static completed =>
                {
                    if (completed.Exception != null) "TCP accept worker failed.".Warn();
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            }
        }

        internal HttpListener Listener { get; }

        internal bool Secure { get; }

        internal bool AdmissionStopped => Volatile.Read(ref _acceptingStopped) != 0;

        public bool BindContext(HttpListenerContext context)
        {
            var req = context.Request;
            var listener = SearchListener(req.Url, out var prefix);

            if (listener == null)
            {
                return false;
            }

            context.Listener = listener;
            context.Connection.Prefix = prefix;
            return true;
        }

        public void UnbindContext(HttpListenerContext context) => context.Listener?.UnregisterContext(context);

        // Called under the endpoint manager's registration lock.
        internal bool IsExclusiveTo(HttpListener owner) => _routes.IsExclusiveTo(owner);

        internal void StopAcceptingIfExclusive(HttpListener owner)
        {
            if (IsExclusiveTo(owner)) StopAccepting();
        }

        internal HttpConnection[] StopAcceptingForDrain()
        {
            StopAccepting();
            lock (_unregistered) return _unregistered.ToArray();
        }

        private void StopAccepting()
        {
            if (Interlocked.Exchange(ref _acceptingStopped, 1) == 0)
                _sock.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // Closing the listening socket also interrupts the blocking macOS accept.
            StopAccepting();
            List<HttpConnection> connections;

            lock (_unregistered)
            {
                // Clone the list because RemoveConnection can be called from Close
                connections = new List<HttpConnection>(_unregistered);
                _unregistered.Clear();
            }

            foreach (var c in connections)
            {
                c.Dispose();
            }
        }

        public bool AddPrefix(ListenerPrefix prefix, HttpListener listener)
        {
            if (Volatile.Read(ref _acceptingStopped) != 0)
                throw new HttpListenerException(995, "The endpoint stopped accepting connections.");
            return _routes.Add(prefix, listener);
        }

        public void RemovePrefix(ListenerPrefix prefix, HttpListener listener)
        {
            _routes.Remove(prefix, listener);
            if (_routes.IsEmpty) EndPointManager.RemoveEndPoint(this, _endpoint);
        }

        internal void RemoveConnection(HttpConnection conn)
        {
            lock (_unregistered)
            {
                _ = _unregistered.Remove(conn);
            }
        }

        private void AcceptOnWorker()
        {
            var reportedInvalidAddress = false;
            while (Volatile.Read(ref _acceptingStopped) == 0)
            {
                Socket accepted;
                try
                {
                    accepted = _sock.Accept();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (ArgumentException exception) when (exception.ParamName == "socketAddress")
                {
                    // A reset can leave macOS with an unusable peer address. Drop that
                    // connection and continue accepting; report once to avoid log flooding.
                    if (!reportedInvalidAddress)
                    {
                        "Discarded an accepted socket with an invalid peer address on macOS.".Warn();
                        reportedInvalidAddress = true;
                    }

                    continue;
                }
                catch (SocketException)
                {
                    if (Volatile.Read(ref _acceptingStopped) != 0)
                        return;

                    // Transient reset/resource errors must not stop the endpoint or spin.
                    Thread.Sleep(100);
                    continue;
                }

                ProcessAcceptedSocket(accepted);
            }
        }

        private void ProcessAcceptedSocket(Socket accepted)
        {
            if (Volatile.Read(ref _acceptingStopped) != 0 || (Secure && Listener.Certificate == null))
            {
                accepted.Dispose();
                return;
            }

            HttpConnection conn;
            try
            {
                conn = new HttpConnection(accepted, this);
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                accepted.Dispose();
                return;
            }

            var registered = false;
            lock (_unregistered)
            {
                if (Volatile.Read(ref _acceptingStopped) == 0)
                {
                    _ = _unregistered.Add(conn);
                    registered = true;
                }
            }

            if (registered)
            {
                // TLS authentication can perform CPU work before its first await.
                // Preserve execution context, but keep that work off the accept actor.
                try
                {
#if NET10_0_OR_GREATER
                    var queued = ThreadPool.QueueUserWorkItem(static connection => { _ = connection.BeginReadRequest(); }, conn, false);
#else
                    var queued = ThreadPool.QueueUserWorkItem(static state =>
                    {
                        var connection = (HttpConnection)(state ?? throw new InvalidOperationException("Missing queued connection."));
                        _ = connection.BeginReadRequest();
                    }, conn);
#endif
                    if (!queued) conn.Dispose();
                }
                catch { conn.Dispose(); throw; }
            }
            else
                conn.Dispose();
        }

        internal HttpListener? SearchListener(Uri uri, out ListenerPrefix? prefix) => _routes.Find(uri, out prefix);
    }
}
