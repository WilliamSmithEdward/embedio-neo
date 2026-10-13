using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // Owns process-wide TCP bindings and each logical listener's prefix leases.
    // Publish/detach is serialized; physical session cleanup is always outside
    // this gate. A failed registration rolls back only its recorded mutations.
    internal static class TcpEndpointRegistry
    {
        private sealed class Lease
        {
            internal Lease(string text, ListenerPrefix prefix, EndPointListener[] endpoints, EndPointListener[] mutations)
            { Text = text; Prefix = prefix; Endpoints = endpoints; Mutations = mutations; }
            internal string Text { get; }
            internal ListenerPrefix Prefix { get; }
            internal EndPointListener[] Endpoints { get; }
            internal EndPointListener[] Mutations { get; }
        }

        // The prefixes one Register call publishes for an owner, with every
        // mutation recorded so a failure undoes only what this call changed.
        // Used under Gate only.
        private sealed class PrefixGeneration
        {
            private readonly HttpListener _owner;
            private readonly Dictionary<string, Lease> _leases;
            private readonly List<Lease> _published = new();
            private readonly List<EndPointListener> _created = new();
            private readonly HashSet<EndPointListener> _joined = new();

            internal PrefixGeneration(HttpListener owner, Dictionary<string, Lease> leases)
            { _owner = owner; _leases = leases; }

            internal void Publish(string text)
            {
                if (_leases.ContainsKey(text)) return;
                var prefix = new ListenerPrefix(text);
                if (!prefix.IsValid()) throw new HttpListenerException(400, "Invalid path.");
                var endpoints = new List<EndPointListener>();
                var mutations = new List<EndPointListener>();
                try
                {
                    foreach (var address in Addresses(prefix.Host))
                    {
                        var endpoint = Join(address, prefix);
                        if (endpoint.AddPrefix(prefix, _owner)) mutations.Add(endpoint);
                        endpoints.Add(endpoint);
                        _joined.Add(endpoint);
                    }
                }
                catch
                {
                    foreach (var endpoint in mutations) endpoint.DetachRoute(prefix, _owner);
                    throw;
                }
                var lease = new Lease(text, prefix, endpoints.ToArray(), mutations.ToArray());
                _leases.Add(text, lease);
                _published.Add(lease);
            }

            // The process-wide endpoint for one address, bound on first use. Another
            // owner's endpoint is joined only when scheme, connection limits and TLS
            // policy let the two listeners share it.
            private EndPointListener Join(IPAddress address, ListenerPrefix prefix)
            {
                var key = new IPEndPoint(address, prefix.Port);
                if (!Bindings.TryGetValue(key, out var endpoint))
                {
                    endpoint = new EndPointListener(_owner, address, prefix.Port, prefix.Secure, false);
                    Bindings.Add(key, endpoint);
                    _created.Add(endpoint);
                }
                if (endpoint.Secure != prefix.Secure)
                    throw new HttpListenerException(400, "HTTP and HTTPS cannot share a listening endpoint.");
                if (ReferenceEquals(endpoint.Listener, _owner)) return endpoint;
                // An endpoint applies its opener's connection limits to every
                // connection, so listeners with their own limits do not share one.
                if (!endpoint.Listener.ConnectionPolicy.IsDefault || !_owner.ConnectionPolicy.IsDefault)
                    throw new HttpListenerException(400, "A listener with connection limits cannot share a listening endpoint with another listener.");
                if (prefix.Secure && !ServerTlsPolicy.CanShareEndpoint(endpoint.Listener.TlsPolicy, _owner.TlsPolicy))
                    throw new HttpListenerException(400, "Listeners with incompatible TLS policies cannot share a listening endpoint.");
                return endpoint;
            }

            // Queue workers only after the whole prefix generation is installed.
            // Scheduling runs no TLS or application callbacks.
            internal void Activate()
            {
                foreach (var endpoint in _joined) endpoint.StartAccepting();
            }

            internal void RollBack(HashSet<EndPointListener> retired)
            {
                foreach (var lease in _published)
                {
                    foreach (var endpoint in lease.Mutations) endpoint.DetachRoute(lease.Prefix, _owner);
                    _leases.Remove(lease.Text);
                }
                foreach (var endpoint in _created)
                    if (endpoint.RoutesEmpty) DetachBinding(endpoint, retired);
            }
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<IPEndPoint, EndPointListener> Bindings = new();
        private static readonly Dictionary<HttpListener, Dictionary<string, Lease>> Owners = new();

        internal static void Register(HttpListener owner, IEnumerable<string> prefixes)
        {
            var retired = new HashSet<EndPointListener>();
            ExceptionDispatchInfo? failure = null;
            lock (Gate)
            {
                var leases = LeasesOf(owner);
                var generation = new PrefixGeneration(owner, leases);
                try
                {
                    foreach (var text in prefixes) generation.Publish(text);
                    generation.Activate();
                }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                    generation.RollBack(retired);
                }
                if (leases.Count == 0) Owners.Remove(owner);
            }
            Release(retired, failure);
        }

        private static Dictionary<string, Lease> LeasesOf(HttpListener owner)
        {
            if (!Owners.TryGetValue(owner, out var leases))
            {
                leases = new Dictionary<string, Lease>(StringComparer.Ordinal);
                Owners.Add(owner, leases);
            }
            return leases;
        }

        internal static void Remove(HttpListener owner)
        {
            var retired = new HashSet<EndPointListener>();
            lock (Gate)
            {
                if (!Owners.TryGetValue(owner, out var leases)) return;
                var endpoints = Endpoints(leases);
                foreach (var endpoint in endpoints) endpoint.StopAcceptingIfExclusive(owner);
                foreach (var lease in leases.Values)
                    foreach (var endpoint in lease.Endpoints) endpoint.DetachRoute(lease.Prefix, owner);
                Owners.Remove(owner);
                foreach (var endpoint in endpoints)
                    if (endpoint.RoutesEmpty) DetachBinding(endpoint, retired);
            }
            Release(retired, null);
        }

        private static void DetachBinding(EndPointListener endpoint, HashSet<EndPointListener> retired)
        {
            var key = endpoint.LocalEndpoint;
            if (Bindings.TryGetValue(key, out var current) && ReferenceEquals(current, endpoint))
                Bindings.Remove(key);
            // Seal physical admission before another registration can reuse the
            // address. Accepted-session disposal is deferred until Gate is free.
            endpoint.StopAcceptingForDrain();
            retired.Add(endpoint);
        }

        private static void Release(IEnumerable<EndPointListener> retired, ExceptionDispatchInfo? failure)
        {
            foreach (var endpoint in retired)
            {
                try { endpoint.Dispose(); }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                { failure ??= ExceptionDispatchInfo.Capture(error); }
            }
            failure?.Throw();
        }

        internal static HashSet<HttpConnection> BeginDrain(HttpListener owner, out HashSet<EndPointListener> exclusive)
        {
            lock (Gate)
            {
                exclusive = new HashSet<EndPointListener>();
                var sessions = new HashSet<HttpConnection>();
                if (!Owners.TryGetValue(owner, out var leases)) return sessions;
                foreach (var endpoint in Endpoints(leases))
                    if (endpoint.IsExclusiveTo(owner)) exclusive.Add(endpoint);
                foreach (var endpoint in exclusive) sessions.UnionWith(endpoint.StopAcceptingForDrain());
                return sessions;
            }
        }

        private static HashSet<EndPointListener> Endpoints(Dictionary<string, Lease> leases)
        {
            var endpoints = new HashSet<EndPointListener>();
            foreach (var lease in leases.Values) endpoints.UnionWith(lease.Endpoints);
            return endpoints;
        }

        internal static EndPointListener[] OwnedEndpoints(HttpListener owner)
        {
            lock (Gate)
                return Owners.TryGetValue(owner, out var leases) ? Endpoints(leases).ToArray() : Array.Empty<EndPointListener>();
        }

        internal static EndPointListener? FindEndpoint(IPAddress address, int port)
        {
            lock (Gate)
                return Bindings.TryGetValue(new IPEndPoint(address, port), out var endpoint) ? endpoint : null;
        }

        internal static bool TryEndpoints(out EndPointListener[] endpoints)
        {
            if (!Monitor.TryEnter(Gate)) { endpoints = Array.Empty<EndPointListener>(); return false; }
            try { endpoints = Bindings.Values.ToArray(); return true; }
            finally { Monitor.Exit(Gate); }
        }

        private static IPAddress[] Addresses(string host)
        {
            var ipv6 = EndPointManager.UseIpv6;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return ipv6 && Socket.OSSupportsIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : new[] { IPAddress.Loopback };
            var fallback = ipv6 ? IPAddress.IPv6Any : IPAddress.Any;
            if (host is "*" or "+" or "0.0.0.0") return new[] { fallback };
            if (IPAddress.TryParse(host, out var literal)) return new[] { literal };
            try
            {
                var resolved = Dns.GetHostAddresses(host);
                return new[] { resolved.Length == 0 ? fallback : resolved[0] };
            }
            catch (Exception error) when (error is SocketException or ArgumentException)
            { return new[] { fallback }; }
        }
    }
}
