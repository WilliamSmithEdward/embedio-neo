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
        private Dictionary<ListenerPrefix, HttpListener> _prefixes;
        private List<ListenerPrefix>? _unhandled; // unhandled; host = '*'
        private List<ListenerPrefix>? _all; //  all;  host = '+

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
            _prefixes = new Dictionary<ListenerPrefix, HttpListener>();
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
                var args = new SocketAsyncEventArgs { UserToken = this };
                args.Completed += OnAccept;
                Accept(_sock, args);
            }
        }

        internal HttpListener Listener { get; }

        internal bool Secure { get; }

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

        public void UnbindContext(HttpListenerContext context) => context.Listener.UnregisterContext(context);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // Closing the listening socket also interrupts the blocking macOS accept.
            _sock.Dispose();
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
            if (prefix.Host == "*")
            {
                AddSpecial(ref _unhandled, prefix, listener);
                return true;
            }

            if (prefix.Host == "+")
            {
                AddSpecial(ref _all, prefix, listener);
                return true;
            }

            Dictionary<ListenerPrefix, HttpListener> prefs, p2;

            do
            {
                prefs = _prefixes;
                var existing = prefs.Keys.FirstOrDefault(p => SamePrefix(p, prefix));
                if (existing != null)
                {
                    if (prefs[existing] != listener)
                    {
                        throw new HttpListenerException(400, $"There is another listener for {prefix}");
                    }

                    return false;
                }

                p2 = new Dictionary<ListenerPrefix, HttpListener>(prefs);
                p2[prefix] = listener;
            }
            while (Interlocked.CompareExchange(ref _prefixes, p2, prefs) != prefs);
            return true;
        }

        public void RemovePrefix(ListenerPrefix prefix, HttpListener listener)
        {
            if (prefix.Host == "*")
            {
                RemoveSpecial(ref _unhandled, prefix, listener);
                CheckIfRemove();
                return;
            }

            if (prefix.Host == "+")
            {
                RemoveSpecial(ref _all, prefix, listener);
                CheckIfRemove();
                return;
            }

            Dictionary<ListenerPrefix, HttpListener> prefs, p2;

            do
            {
                prefs = _prefixes;
                var prefixKey = prefs.Keys.FirstOrDefault(p => SamePrefix(p, prefix) && prefs[p] == listener);

                if (prefixKey is null)
                {
                    break;
                }

                p2 = new Dictionary<ListenerPrefix, HttpListener>(prefs);
                _ = p2.Remove(prefixKey);
            }
            while (Interlocked.CompareExchange(ref _prefixes, p2, prefs) != prefs);

            CheckIfRemove();
        }

        internal void RemoveConnection(HttpConnection conn)
        {
            lock (_unregistered)
            {
                _ = _unregistered.Remove(conn);
            }
        }

        private static void Accept(Socket socket, SocketAsyncEventArgs e, Socket? accepted = null)
        {
            var endpoint = (EndPointListener)e.UserToken;
            while (true)
            {
                e.AcceptSocket = null;
                bool acceptPending;
                try
                {
                    acceptPending = socket.AcceptAsync(e);
                }
                catch
                {
                    accepted?.Dispose();
                    accepted = null;
                    return;
                }

                // Rearm before handling the previous socket, including when the next
                // accept completes inline. Drain inline completions without recursion.
                if (accepted != null)
                {
                    endpoint.ProcessAcceptedSocket(accepted);
                    accepted = null;
                }

                if (acceptPending)
                    return;

                if (e.SocketError == SocketError.Success)
                    accepted = e.AcceptSocket;
            }
        }

        private static void ProcessAccept(SocketAsyncEventArgs args)
        {
            var accepted = args.SocketError == SocketError.Success ? args.AcceptSocket : null;
            var endpoint = (EndPointListener)args.UserToken;
            Accept(endpoint._sock, args, accepted);
        }

        private void AcceptOnWorker()
        {
            var reportedInvalidAddress = false;
            while (Volatile.Read(ref _disposed) == 0)
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
                    if (Volatile.Read(ref _disposed) != 0)
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
            if (Volatile.Read(ref _disposed) != 0 || (Secure && Listener.Certificate == null))
            {
                accepted.Dispose();
                return;
            }

            HttpConnection conn;
            try
            {
                conn = new HttpConnection(accepted, this);
            }
            catch
            {
                accepted.Dispose();
                return;
            }

            var registered = false;
            lock (_unregistered)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _ = _unregistered.Add(conn);
                    registered = true;
                }
            }

            if (registered)
                _ = conn.BeginReadRequest();
            else
                conn.Dispose();
        }

        private static void OnAccept(object sender, SocketAsyncEventArgs e) => ProcessAccept(e);

        private static HttpListener? MatchFromList(string path, List<ListenerPrefix>? list, out ListenerPrefix? prefix)
        {
            prefix = null;
            if (list == null)
            {
                return null;
            }

            HttpListener? bestMatch = null;
            var bestLength = -1;

            foreach (var p in list)
            {
                if (p.Path.Length < bestLength || !path.StartsWith(p.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                bestLength = p.Path.Length;
                bestMatch = p.Listener;
                prefix = p;
            }

            return bestMatch;
        }

        private static void AddSpecial(ref List<ListenerPrefix>? prefixes, ListenerPrefix prefix, HttpListener listener)
        {
            prefix.Listener = listener;
            List<ListenerPrefix>? current;
            List<ListenerPrefix> future;
            do
            {
                current = prefixes;
                future = current == null ? new List<ListenerPrefix>() : new List<ListenerPrefix>(current);
                if (future.Any(p => p.Path == prefix.Path))
                    throw new HttpListenerException(400, "Prefix already in use.");
                future.Add(prefix);
            }
            while (Interlocked.CompareExchange(ref prefixes, future, current) != current);
        }

        private static void RemoveSpecial(ref List<ListenerPrefix>? prefixes, ListenerPrefix prefix, HttpListener listener)
        {
            List<ListenerPrefix>? current;
            List<ListenerPrefix> future;
            do
            {
                current = prefixes;
                var index = current?.FindIndex(p => p.Path == prefix.Path && p.Listener == listener) ?? -1;
                if (index < 0)
                    return;
                future = new List<ListenerPrefix>(current!);
                future.RemoveAt(index);
            }
            while (Interlocked.CompareExchange(ref prefixes, future, current) != current);
        }

        private static bool SamePrefix(ListenerPrefix first, ListenerPrefix second)
            => first.Host == second.Host && first.Port == second.Port
                && first.Path == second.Path && first.Secure == second.Secure;

        private HttpListener? SearchListener(Uri uri, out ListenerPrefix? prefix)
        {
            prefix = null;
            if (uri == null)
            {
                return null;
            }

            var host = uri.Host;
            var port = uri.Port;
            var path = WebUtility.UrlDecode(uri.AbsolutePath);
            var pathSlash = path[path.Length - 1] == '/' ? path : path + "/";

            HttpListener? bestMatch = null;
            var bestLength = -1;

            if (!string.IsNullOrEmpty(host))
            {
                var result = _prefixes;

                foreach (var p in result.Keys)
                {
                    if (p.Path.Length < bestLength)
                    {
                        continue;
                    }

                    if (p.Host != host || p.Port != port)
                    {
                        continue;
                    }

                    if (!path.StartsWith(p.Path, StringComparison.Ordinal) && !pathSlash.StartsWith(p.Path, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bestLength = p.Path.Length;
                    bestMatch = result[p];
                    prefix = p;
                }

                if (bestLength != -1)
                {
                    return bestMatch;
                }
            }

            var list = _unhandled;
            bestMatch = MatchFromList(path, list, out prefix);
            if (path != pathSlash && bestMatch == null)
            {
                bestMatch = MatchFromList(pathSlash, list, out prefix);
            }

            if (bestMatch != null)
            {
                return bestMatch;
            }

            list = _all;
            bestMatch = MatchFromList(path, list, out prefix);
            if (path != pathSlash && bestMatch == null)
            {
                bestMatch = MatchFromList(pathSlash, list, out prefix);
            }

            return bestMatch;
        }

        private void CheckIfRemove()
        {
            if (_prefixes.Count > 0)
            {
                return;
            }

            var list = _unhandled;
            if (list != null && list.Count > 0)
            {
                return;
            }

            list = _all;
            if (list != null && list.Count > 0)
            {
                return;
            }

            EndPointManager.RemoveEndPoint(this, _endpoint);
        }
    }
}
