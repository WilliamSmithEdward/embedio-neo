using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using EmbedIO.Net.Internal;
using EmbedIO.Diagnostics;

namespace EmbedIO.Net
{
    /// <summary>
    /// Represents the EndPoint Manager.
    /// </summary>
    public static class EndPointManager
    {
        private static readonly ConcurrentDictionary<IPAddress, ConcurrentDictionary<int, EndPointListener>> IPToEndpoints = new();
        private static readonly object RegistrationLock = new();
        private static readonly Dictionary<HttpListener, Dictionary<string, List<EndPointListener>>> Registrations = new();

        /// <summary>
        /// Gets or sets a value indicating whether [use IPv6]. By default, this flag is set.
        /// </summary>
        /// <value>
        ///   <c>true</c> if [use IPv6]; otherwise, <c>false</c>.
        /// </value>
        public static bool UseIpv6 { get; set; } = true;

        internal static void AddListener(HttpListener listener)
        {
            var added = new List<string>();

            try
            {
                foreach (var prefix in listener.Prefixes)
                {
                    AddPrefix(prefix, listener);
                    added.Add(prefix);
                }
            }
            catch (Exception ex)
            {
                ex.Log(nameof(AddListener));

                foreach (var prefix in added)
                {
                    RemovePrefix(prefix, listener);
                }

                throw;
            }
        }

        internal static void RemoveEndPoint(EndPointListener epl, IPEndPoint ep)
        {
            lock (RegistrationLock)
            {
                // Canonical aliases can retain an endpoint after an earlier alias
                // released it. Removal must not erase a replacement at the same port.
                if (IPToEndpoints.TryGetValue(ep.Address, out var p)
                    && p.TryGetValue(ep.Port, out var current) && ReferenceEquals(current, epl)
                    && p.TryRemove(ep.Port, out _) && p.Count == 0)
                {
                    _ = IPToEndpoints.TryRemove(ep.Address, out _);
                }
            }

            epl.Dispose();
        }

        internal static void StopExclusiveEndpoints(HttpListener listener)
        {
            lock (RegistrationLock)
            {
                if (!Registrations.TryGetValue(listener, out var prefixes)) return;
                var endpoints = new HashSet<EndPointListener>();
                foreach (var registered in prefixes.Values)
                    foreach (var endpoint in registered)
                        if (endpoints.Add(endpoint)) endpoint.StopAcceptingIfExclusive(listener);
            }
        }

        internal static HashSet<HttpConnection> BeginExclusiveDrain(HttpListener listener)
        {
            lock (RegistrationLock)
            {
                var connections = new HashSet<HttpConnection>();
                if (!Registrations.TryGetValue(listener, out var prefixes)) return connections;
                var endpoints = new HashSet<EndPointListener>();
                foreach (var registered in prefixes.Values)
                    foreach (var endpoint in registered)
                    {
                        if (!endpoint.IsExclusiveTo(listener))
                            throw new NotSupportedException("Graceful TCP drain currently requires exclusive endpoint ownership.");
                        endpoints.Add(endpoint);
                    }
                // Validate all endpoints before changing any admission state.
                foreach (var endpoint in endpoints)
                    connections.UnionWith(endpoint.StopAcceptingForDrain());
                return connections;
            }
        }

        internal static void RemoveListener(HttpListener listener)
        {
            foreach (var prefix in listener.Prefixes)
            {
                RemovePrefix(prefix, listener);
            }
        }

        internal static void AddPrefix(string p, HttpListener listener)
        {
            var lp = new ListenerPrefix(p);

            if (!lp.IsValid())
            {
                throw new HttpListenerException(400, "Invalid path.");
            }

            lock (RegistrationLock)
            {
                if (Registrations.TryGetValue(listener, out var prefixes) && prefixes.ContainsKey(p))
                    return;
                var endpoints = new List<EndPointListener>();
                var added = new List<EndPointListener>();
                try
                {
                    foreach (var address in ResolveAddresses(lp.Host))
                    {
                        var ports = IPToEndpoints.GetOrAdd(address, _ => new ConcurrentDictionary<int, EndPointListener>());
                        var endpoint = ports.GetOrAdd(lp.Port, port => new EndPointListener(listener, address, port, lp.Secure));
                        if (endpoint.Secure != lp.Secure)
                            throw new HttpListenerException(400, "HTTP and HTTPS cannot share a listening endpoint.");
                        if (endpoint.AddPrefix(lp, listener))
                            added.Add(endpoint);
                        endpoints.Add(endpoint);
                    }
                }
                catch
                {
                    foreach (var endpoint in added)
                        endpoint.RemovePrefix(lp, listener);
                    throw;
                }

                if (prefixes == null)
                {
                    prefixes = new Dictionary<string, List<EndPointListener>>(StringComparer.Ordinal);
                    Registrations.Add(listener, prefixes);
                }
                prefixes.Add(p, endpoints);
            }
        }

        private static IEnumerable<IPAddress> ResolveAddresses(string host)
        {
            // Localhost is loopback on both families; do not depend on DNS result order.
            // Preserve existing resolution/binding scope for every other hostname.
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                yield return IPAddress.Loopback;
                if (UseIpv6 && Socket.OSSupportsIPv6)
                    yield return IPAddress.IPv6Loopback;
                yield break;
            }
            yield return ResolveAddress(host);
        }

        private static IPAddress ResolveAddress(string host)
        {
            if (host == "*" || host == "+" || host == "0.0.0.0")
            {
                return UseIpv6 ? IPAddress.IPv6Any : IPAddress.Any;
            }

            if (IPAddress.TryParse(host, out var address))
            {
                return address;
            }

            try
            {
                var hostEntry = new IPHostEntry
                {
                    HostName = host,
                    AddressList = Dns.GetHostAddresses(host),
                };

                return hostEntry.AddressList.Length == 0
                    ? UseIpv6 ? IPAddress.IPv6Any : IPAddress.Any
                    : hostEntry.AddressList[0];
            }
            catch (Exception error) when (error is SocketException or ArgumentException)
            {
                return UseIpv6 ? IPAddress.IPv6Any : IPAddress.Any;
            }
        }

        private static void RemovePrefix(string prefix, HttpListener listener)
        {
            lock (RegistrationLock)
            {
                if (!Registrations.TryGetValue(listener, out var prefixes)
                    || !prefixes.TryGetValue(prefix, out var endpoints))
                    return;
                var parsed = new ListenerPrefix(prefix);
                foreach (var endpoint in endpoints)
                    endpoint.RemovePrefix(parsed, listener);
                prefixes.Remove(prefix);
                if (prefixes.Count == 0)
                    Registrations.Remove(listener);
            }
        }
    }
}
