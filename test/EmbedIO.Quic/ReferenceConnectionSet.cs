using System.Net;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    // Deterministic reference driver for malformed-packet and native TLS tests.
    // The live UDP host uses the scheduled source dispatcher.
    internal class QuicConnectionSet : IDisposable
    {
        private readonly QuicConnectionFactory _createConnection;
        private readonly int _capacity;
        private readonly double _handshakeTimeout;

        internal QuicConnectionSet(
            QuicConnectionFactory createConnection,
            int capacity = 16,
            double handshakeTimeout = 3000
        )
        {
            ArgumentNullException.ThrowIfNull(createConnection);
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65536);

            if (!double.IsFinite(handshakeTimeout) || handshakeTimeout <= 0)
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));

            _createConnection = createConnection;
            _capacity = capacity;
            _handshakeTimeout = handshakeTimeout;
        }

        private readonly Dictionary<string, Entry> _connections = new(StringComparer.Ordinal);
        private readonly Dictionary<(IPEndPoint Peer, string Id), Entry> _initials = [];
        private double _window;
        private int _attempts;
        private bool _disposed;

        internal int Active => _connections.Count;
        internal int Accepted { get; private set; }
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
                // Path validation/migration requires coordinated listener support.
                if (!peer.Equals(entry.Peer)) return Reject();

                if (Expired(entry, now))
                {
                    Retire(entry);
                    return Reject();
                }
            }

            else
            {
                if (!initial || bytes.Length < 1200 || Active >= _capacity) return Reject();

                if (now >= _window + 1000)
                {
                    _window = now;
                    _attempts = 0;
                }

                if (_attempts >= _capacity) return Reject();

                _attempts++;
                QuicConnection connection;

                try
                {
                    connection = _createConnection(bytes, now);
                }

                catch (Exception error) when (error is InvalidDataException or CryptographicException)
                {
                    return Reject();
                }

                var serverId = Convert.ToHexString(connection.ServerId);
                var ownedPeer = new IPEndPoint(peer.Address, peer.Port);
                entry = new Entry(serverId, id, ownedPeer, connection, now + _handshakeTimeout);

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

            try
            {
                OnRetiring(entry.Connection);
            }

            finally
            {
                try
                {
                    entry.Connection.Dispose();
                }

                finally
                {
                    Retired++;
                }
            }
        }

        protected virtual void OnRetiring(QuicConnection connection) { }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            List<Exception>? failures = null;

            foreach (var entry in _connections.Values.ToArray())
            {
                try
                {
                    Retire(entry);
                }

                catch (Exception error) when (
                    error is not OutOfMemoryException
                        and not StackOverflowException
                        and not AccessViolationException
                )
                {
                    (failures ??= []).Add(error);
                }
            }

            if (failures != null) throw new AggregateException("QUIC connection cleanup failed.", failures);
        }

        private sealed record Entry(
            string Id,
            string Original,
            IPEndPoint Peer,
            QuicConnection Connection,
            double HandshakeDeadline
        );
    }
}
