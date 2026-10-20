using System.Net;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    internal sealed record OutboundDatagram(IPEndPoint Peer, byte[] Bytes);

    // One event-loop owner. Original destination aliases include the peer endpoint;
    // issued destination IDs select an owner before the peer address is checked.
    internal sealed class ConnectionSet(
        string library,
        string certificate,
        string key,
        bool webTransport = false,
        string authorization = ""
    ) : IDisposable
    {
        internal const int Capacity = 16;
        internal const double HandshakeTimeout = 3000;
        private readonly Dictionary<string, Entry> _connections = new(StringComparer.Ordinal);
        private readonly Dictionary<(IPEndPoint Peer, string Id), Entry> _initials = [];
        private double _window;
        private int _attempts;
        private bool _disposed;

        internal int Active => _connections.Count;
        internal int Accepted { get; private set; }
        internal int Completed { get; private set; }
        internal int Retired { get; private set; }
        internal int Peak { get; private set; }
        internal int Rejected { get; private set; }
        internal int ExpiredHandshakes { get; private set; }
        internal int FailedOwners { get; private set; }

        internal List<OutboundDatagram> Receive(ReadOnlySpan<byte> bytes, IPEndPoint peer, double now)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!PacketHeader.TryDestination(bytes, out var destination, out var initial)) return Reject();

            var id = Convert.ToHexString(destination);

            if (!_connections.TryGetValue(id, out var entry)) _initials.TryGetValue((peer, id), out entry);

            if (entry != null)
            {
                // Path validation/migration is outside this host's supported profile.
                if (!peer.Equals(entry.Peer)) return Reject();

                if (Expired(entry, now))
                {
                    Retire(entry);
                    return Reject();
                }
            }

            else
            {
                if (!initial || bytes.Length < 1200 || Active >= Capacity) return Reject();

                if (now >= _window + 1000)
                {
                    _window = now;
                    _attempts = 0;
                }

                if (_attempts >= Capacity) return Reject();

                _attempts++;
                ProbeConnection connection;

                try
                {
                    connection = new ProbeConnection(
                        bytes,
                        library,
                        certificate,
                        key,
                        webTransport,
                        authorization,
                        now
                    );
                }

                catch (Exception error) when (error is InvalidDataException or CryptographicException)
                {
                    return Reject();
                }

                var serverId = Convert.ToHexString(connection.ServerId);
                var ownedPeer = new IPEndPoint(peer.Address, peer.Port);
                entry = new Entry(serverId, id, ownedPeer, connection, now + HandshakeTimeout);

                if (_initials.Keys.Any(alias => alias.Id == serverId) || !_connections.TryAdd(serverId, entry))
                {
                    connection.Dispose();
                    return Reject();
                }

                _initials.Add((ownedPeer, id), entry);
                Accepted++;
                Peak = Math.Max(Peak, Active);
            }

            List<byte[]> packets;

            try
            {
                packets = entry.Connection.Receive(bytes, now);
            }

            catch (InvalidDataException)
            {
                // The connection already attempted a protocol close. A terminal
                // profile budget can prevent even that packet from being emitted.
                Fail(entry);
                return [];
            }

            if (entry.Connection.Closed) Retire(entry);

            return Packets(entry, packets);
        }

        internal List<OutboundDatagram> Tick(double now)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var output = new List<OutboundDatagram>();

            foreach (var entry in _connections.Values.ToArray())
            {
                if (Expired(entry, now))
                {
                    Retire(entry);
                    continue;
                }

                List<byte[]> packets;

                try
                {
                    packets = entry.Connection.Tick(now);
                }

                catch (InvalidDataException)
                {
                    try
                    {
                        packets = entry.Connection.CloseProtocol(now);
                    }

                    catch (InvalidDataException)
                    {
                        Fail(entry);
                        continue;
                    }
                }

                output.AddRange(Packets(entry, packets));

                if (entry.Connection.Closed) Retire(entry);
            }

            return output;
        }

        private bool Expired(Entry entry, double now)
        {
            if (now < entry.HandshakeDeadline || entry.Connection.Established) return false;

            ExpiredHandshakes++;
            return true;
        }

        private List<OutboundDatagram> Reject()
        {
            Rejected++;
            return [];
        }

        private static List<OutboundDatagram> Packets(Entry entry, List<byte[]> packets) =>
        [
            .. packets.Select(bytes => new OutboundDatagram(entry.Peer, bytes))
        ];

        private void Fail(Entry entry)
        {
            FailedOwners++;
            Retire(entry);
        }

        private void Retire(Entry entry)
        {
            if (!_connections.Remove(entry.Id)) return;

            _initials.Remove((entry.Peer, entry.Original));

            if (entry.Connection.Complete) Completed++;

            entry.Connection.Dispose();
            Retired++;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            foreach (var entry in _connections.Values.ToArray()) Retire(entry);
        }

        private sealed record Entry(
            string Id,
            string Original,
            IPEndPoint Peer,
            ProbeConnection Connection,
            double HandshakeDeadline
        );
    }
}
