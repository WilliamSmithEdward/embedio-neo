using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    internal sealed record QuicPathRoute(ulong Id, QuicConnectionEndpoints Endpoints);

    internal sealed class QuicPath
    {
        internal QuicPath(QuicPathRoute route) => Route = route;

        internal QuicPathRoute Route { get; }
        internal long Received { get; set; }
        internal long Sent { get; set; }
        internal bool AddressValidated { get; set; }
        internal bool MtuValidated { get; set; }
        internal bool Validating { get; set; }
        internal byte[] IncomingId { get; set; } = [];
        internal ulong? PeerSequence { get; set; }
        internal bool RequireUnusedId { get; set; }
        internal Delivery? Pending { get; set; }
        internal List<(byte[] Data, int Size)> Challenges { get; } = [];
        internal int Attempts { get; set; }
        internal double Deadline { get; set; } = double.PositiveInfinity;
        internal double NextProbe { get; set; }
        internal double ProbeInterval { get; set; }
        internal long Allowance => AddressValidated ? long.MaxValue : Math.Max(0, Received * 3 - Sent);
    }

    // Only the connection owner mutates paths, after packet authentication. Route
    // snapshots contain no mutable validation state and change only with membership.
    internal sealed class QuicPathValidation
    {
        internal QuicPathValidation(QuicConnectionEndpoints initial) =>
            _paths = [new(new QuicPathRoute(0, initial)) { PeerSequence = 0 }];

        internal const int Capacity = 3;
        private const int RecentChallengeLimit = 24;
        private readonly List<QuicPath> _paths;
        private readonly Queue<byte[]> _recent = new();
        private readonly Queue<(QuicConnectionEndpoints Endpoints, double Until)> _failed = new();
        private IReadOnlyList<QuicPathRoute>? _snapshot;
        private ulong _nextId = 1;
        private ulong _current;
        private ulong _fallback;
        private ulong? _largest;

        internal QuicPath Current => Find(_current) ?? throw new InvalidOperationException("Missing current path.");
        internal QuicPath Fallback => Find(_fallback) ?? throw new InvalidOperationException("Missing fallback path.");
        internal int Count => _paths.Count;
        internal int RetainedChallenges => _recent.Count + _paths.Sum(static path => path.Challenges.Count);
        internal IReadOnlyList<QuicPathRoute> Snapshot => _snapshot ??= [.. _paths.Select(static path => path.Route)];

        internal bool Contains(QuicConnectionEndpoints endpoints)
        {
            foreach (var path in _paths)
                if (Same(path.Route.Endpoints, endpoints)) return true;

            return false;
        }

        internal QuicPath? Find(ulong id)
        {
            foreach (var path in _paths)
                if (path.Route.Id == id) return path;

            return null;
        }

        internal int CopyPeerSequences(Span<ulong> sequences)
        {
            var count = 0;

            foreach (var path in _paths)
                if (path.PeerSequence.HasValue) sequences[count++] = path.PeerSequence.Value;

            return count;
        }

        internal ulong? SharedPeerSequence(QuicPath path, PeerConnectionIds ids)
        {
            foreach (var candidate in _paths)
                if (
                    candidate != path
                    && candidate.PeerSequence is { } sequence
                    && ids.IsActive(sequence)
                    && path.IncomingId
                        .AsSpan()
                        .SequenceEqual(candidate.IncomingId)
                ) return sequence;

            return null;
        }

        internal static bool Same(QuicConnectionEndpoints left, QuicConnectionEndpoints right) =>
            left.Peer.Equals(right.Peer) && Equals(left.Local, right.Local);

        internal QuicPath? Receive(
            QuicConnectionEndpoints endpoints,
            ReadOnlySpan<byte> destination,
            int bytes,
            bool confirmed,
            double now = 0
        )
        {
            while (_failed.TryPeek(out var failure) && now >= failure.Until) _failed.Dequeue();

            foreach (var (Endpoints, Until) in _failed)
                if (Until > now && Same(Endpoints, endpoints)) return null;

            QuicPath? path = null;

            foreach (var candidate in _paths)
                if (Same(candidate.Route.Endpoints, endpoints))
                {
                    path = candidate;
                    break;
                }

            if (path == null)
            {
                if (!confirmed || !Equals(_paths[0].Route.Endpoints.Local, endpoints.Local)) return null;

                if (_paths.Count == Capacity)
                {
                    var expendable = _paths.FirstOrDefault(
                        value => value.Route.Id != _current && value.Route.Id != _fallback
                    );

                    if (expendable == null) return null;

                    Remove(expendable);
                }

                QuicPath? previous = null;

                foreach (var candidate in _paths)
                    if (destination.SequenceEqual(candidate.IncomingId)) previous = candidate;

                path = new QuicPath(new QuicPathRoute(_nextId++, endpoints))
                {
                    PeerSequence = previous?.PeerSequence,
                    RequireUnusedId = previous == null
                };
                _paths.Add(path);
                _snapshot = null;
            }

            path.Received = Math.Min(long.MaxValue / 3, path.Received + bytes);

            if (!destination.SequenceEqual(path.IncomingId)) path.IncomingId = destination.ToArray();

            return path;
        }

        internal void ValidateInitial()
        {
            var initialPath = Find(0) ?? throw new InvalidOperationException("Missing initial path.");
            initialPath.AddressValidated = initialPath.MtuValidated = true;
        }

        internal bool Observe(QuicPath path, ulong number, bool nonProbing, double now, double pto)
        {
            var newest = !_largest.HasValue || number > _largest.Value;
            _largest = Math.Max(_largest ?? 0, number);

            if (!newest || !nonProbing || path.Route.Id == _current) return false;

            var previous = Current;
            _current = path.Route.Id;
            Start(path, now, pto);
            // A forwarded packet must not silently replace the previously active path.
            Start(previous, now, pto);
            Prune(previous.Route.Id);
            return true;
        }

        private static void Start(QuicPath path, double now, double pto)
        {
            if (path.Validating) return;

            path.Validating = true;
            path.Attempts = 0;
            path.NextProbe = now;
            path.ProbeInterval = Math.Max(1024, pto);
            path.Deadline = now + 3 * path.ProbeInterval;
        }

        internal IReadOnlyList<Delivery> Probes(double now)
        {
            List<Delivery>? output = null;

            foreach (var path in _paths)
            {
                if (!path.Validating || path.Pending != null || path.Attempts >= 3 || now < path.NextProbe) continue;

                var bytes = new byte[9];
                bytes[0] = 0x1a;
                RandomNumberGenerator.Fill(bytes.AsSpan(1));
                var delivery = new Delivery(2, bytes, false)
                {
                    PadToMinimum = true,
                    Path = path.Route
                };
                path.Pending = delivery;
                (output ??= []).Add(delivery);
            }

            return output ?? (IReadOnlyList<Delivery>)[];
        }

        internal static void Sent(QuicPath path, Delivery delivery, int bytes, double now)
        {
            path.Sent = Math.Min(long.MaxValue / 3, path.Sent + bytes);

            if (!ReferenceEquals(path.Pending, delivery)) return;

            path.Pending = null;
            path.Challenges.Add(
                (
                    delivery.Bytes
                        .AsSpan(1, 8)
                        .ToArray(),
                    bytes
                )
            );
            path.Attempts++;
            path.NextProbe = now + path.ProbeInterval;

            if (double.IsPositiveInfinity(path.Deadline)) path.Deadline = now + 3 * path.ProbeInterval;
        }

        internal QuicPath? Respond(ReadOnlySpan<byte> data, double now)
        {
            foreach (var path in _paths)
            {
                var matched = -1;

                for (var index = 0; index < path.Challenges.Count; index++)
                    if (CryptographicOperations.FixedTimeEquals(data, path.Challenges[index].Data)) matched = index;

                if (matched < 0) continue;

                var full = path.Challenges[matched].Size >= 1200;
                path.AddressValidated = true;
                path.MtuValidated |= full;
                Forget(path);

                if (path.MtuValidated)
                {
                    path.Validating = false;

                    if (path.Route.Id == _current) _fallback = _current;

                    Prune();
                }

                else
                {
                    // Address proof from a small probe does not prove a 1200-byte path.
                    path.Attempts = 0;
                    path.Deadline = now + 3 * path.ProbeInterval;
                    path.NextProbe = now;
                }

                return path;
            }

            foreach (var recent in _recent)
                if (CryptographicOperations.FixedTimeEquals(data, recent)) return null;

            throw new TransportException(10, "Unmatched path response.");
        }

        internal bool Tick(double now)
        {
            var changed = false;

            foreach (var path in _paths.ToArray())
            {
                if (!path.Validating || now < path.Deadline) continue;

                path.Validating = false;
                Forget(path);

                if (path.Route.Id == _current && path.Route.Id != _fallback)
                {
                    _current = _fallback;
                    changed = true;
                }

                if (path.Route.Id != _current && path.Route.Id != _fallback)
                {
                    _failed.Enqueue((path.Route.Endpoints, now + 3 * path.ProbeInterval));

                    while (_failed.Count > Capacity) _failed.Dequeue();

                    Remove(path);
                }
            }

            return changed;
        }

        private void Prune(ulong? previous = null)
        {
            for (var index = _paths.Count - 1; index >= 0; index--)
            {
                var path = _paths[index];

                if (path.Route.Id != _current && path.Route.Id != _fallback && path.Route.Id != previous)
                    Remove(path);
            }
        }

        private void Forget(QuicPath path)
        {
            if (path.Pending is { } pending)
            {
                pending.Acknowledged = true;
                path.Pending = null;
            }

            foreach (var (data, _) in path.Challenges)
            {
                _recent.Enqueue(data);

                while (_recent.Count > RecentChallengeLimit) _recent.Dequeue();
            }

            path.Challenges.Clear();
        }

        private void Remove(QuicPath path)
        {
            Forget(path);
            _paths.Remove(path);
            _snapshot = null;
        }
    }
}
