using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace EmbedIO.Security.Internal
{
    // Immutable network bytes, never a collection of all addresses in a subnet.
    internal sealed class ClientNetwork
    {
        private readonly byte[] _bytes;
        private readonly int _prefix;

        private ClientNetwork(byte[] bytes, int prefix) { _bytes = bytes; _prefix = prefix; }

        internal static ClientNetwork Parse(string? network)
        {
            if (network == null || string.IsNullOrWhiteSpace(network) || network.Trim() != network || network.IndexOf("%", StringComparison.Ordinal) >= 0)
                throw new ArgumentException("Expected an IP literal or CIDR without a scope identifier.", nameof(network));
            var parts = network.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
                throw new ArgumentException("Expected an IP literal or CIDR, not a hostname.", nameof(network));
            var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (bits == 32 && address.ToString() != parts[0])
                throw new ArgumentException("IPv4 addresses must use canonical dotted decimal.", nameof(network));
            var prefix = bits;
            if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
                || prefix > bits))
                throw new ArgumentException("The CIDR prefix is outside its address family.", nameof(network));
            if (address.IsIPv4MappedToIPv6)
            {
                if (prefix < 96)
                    throw new ArgumentException("Mapped IPv4 CIDRs require at least 96 prefix bits.", nameof(network));
                address = address.MapToIPv4();
                prefix -= 96;
            }
            var bytes = address.GetAddressBytes();
            for (var i = 0; i < bytes.Length; i++)
            {
                var significant = Math.Max(0, Math.Min(8, prefix - i * 8));
                bytes[i] &= (byte)(255 << (8 - significant));
            }
            return new ClientNetwork(bytes, prefix);
        }

        internal bool Contains(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var bytes = address.GetAddressBytes();
            if (bytes.Length != _bytes.Length) return false;
            for (var i = 0; i < bytes.Length; i++)
            {
                var significant = Math.Max(0, Math.Min(8, _prefix - i * 8));
                if ((bytes[i] & (byte)(255 << (8 - significant))) != _bytes[i]) return false;
            }
            return true;
        }
    }
}
