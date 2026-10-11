using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // One gate defines the publication boundary for pending sessions. Stop seals
    // that boundary before releasing the listening socket; shutdown owns the
    // returned snapshot and never calls a session while holding this gate.
    internal sealed class TcpEndpointAdmission
    {
        private readonly object _sync = new();
        private readonly HashSet<HttpConnection> _pending = new();
        private int _phase; // accepting, sealed, released

        internal TcpEndpointAdmission(Socket socket) => Socket = socket ?? throw new ArgumentNullException(nameof(socket));
        internal Socket Socket { get; }
        internal bool IsStopped => Volatile.Read(ref _phase) != 0;

        internal static TcpEndpointAdmission Bind(IPAddress address, int port)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6 && EndPointManager.UseIpv6)
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                socket.Bind(new IPEndPoint(address, port));
                socket.Listen(500);
                return new TcpEndpointAdmission(socket);
            }
            catch { socket.Dispose(); throw; }
        }

        internal HttpConnection? CreatePending(Socket peer, EndPointListener endpoint)
        {
            lock (_sync)
            {
                if (_phase != 0) return null;
                // Construction only creates owned transport wrappers and the head
                // timer. TLS, reads and application admission start after this gate.
                var connection = new HttpConnection(peer, endpoint);
                _pending.Add(connection);
                return connection;
            }
        }

        internal void Release(HttpConnection connection)
        {
            lock (_sync) _pending.Remove(connection);
        }

        internal void Stop()
        {
            lock (_sync) { if (_phase == 0) Volatile.Write(ref _phase, 1); }
            // Repeated disposal is intentional: a concurrent Stop must not return
            // before the listening socket has actually been closed by either caller.
            Socket.Dispose();
        }

        internal HttpConnection[] StopAndSnapshot()
        {
            HttpConnection[] snapshot;
            lock (_sync)
            {
                if (_phase == 0) Volatile.Write(ref _phase, 1);
                snapshot = Snapshot();
            }
            Socket.Dispose();
            return snapshot;
        }

        internal HttpConnection[] CloseAndSnapshot(out ExceptionDispatchInfo? failure)
        {
            HttpConnection[] snapshot;
            lock (_sync)
            {
                Volatile.Write(ref _phase, 2);
                snapshot = Snapshot();
                _pending.Clear();
            }
            failure = null;
            try { Socket.Dispose(); }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            { failure = ExceptionDispatchInfo.Capture(error); }
            return snapshot;
        }

        internal HttpConnection[] PendingSnapshot()
        {
            lock (_sync) return Snapshot();
        }

        internal bool TryPendingSnapshot(out HttpConnection[] connections)
        {
            connections = Array.Empty<HttpConnection>();
            if (!Monitor.TryEnter(_sync)) return false;
            try { connections = Snapshot(); return true; }
            finally { Monitor.Exit(_sync); }
        }

        private HttpConnection[] Snapshot()
        {
            if (_pending.Count == 0) return Array.Empty<HttpConnection>();
            var connections = new HttpConnection[_pending.Count];
            _pending.CopyTo(connections);
            return connections;
        }
    }
}
