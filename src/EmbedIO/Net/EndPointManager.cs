using System;
using System.Collections.Generic;
using System.Net;
using EmbedIO.Diagnostics;
using EmbedIO.Net.Internal;

namespace EmbedIO.Net
{
    /// <summary>Configures address-family selection for managed TCP listeners.</summary>
    public static class EndPointManager
    {
        /// <summary>Gets or sets whether wildcard bindings and localhost use IPv6. The default is true.</summary>
        public static bool UseIpv6 { get; set; } = true;

        internal static void AddListener(HttpListener listener)
        {
            try { TcpEndpointRegistry.Register(listener, listener.Prefixes); }
            catch (Exception error) { error.Log(nameof(AddListener)); throw; }
        }

        internal static void AddPrefix(string prefix, HttpListener listener)
            => TcpEndpointRegistry.Register(listener, new[] { prefix });

        internal static void RemoveListener(HttpListener listener) => TcpEndpointRegistry.Remove(listener);

        internal static HashSet<HttpConnection> BeginDrain(HttpListener listener, out HashSet<EndPointListener> exclusive)
            => TcpEndpointRegistry.BeginDrain(listener, out exclusive);

        internal static EndPointListener[] OwnedEndpoints(HttpListener listener) => TcpEndpointRegistry.OwnedEndpoints(listener);
        internal static EndPointListener? FindEndpoint(IPAddress address, int port) => TcpEndpointRegistry.FindEndpoint(address, port);
        internal static bool TryEndpoints(out EndPointListener[] endpoints) => TcpEndpointRegistry.TryEndpoints(out endpoints);
    }
}
