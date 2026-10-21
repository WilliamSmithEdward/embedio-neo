using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    // RFC 9000 5.1/19.15: active IDs and unacknowledged retirements have separate
    // bounds. Retired sequence numbers are represented by a watermark, not history.
    internal sealed class PeerConnectionIds(
        byte[] initial,
        Func<ulong, Delivery> retire,
        bool initialUsed = false
    ) : IDisposable
    {
        internal const int ActiveLimit = 2;
        internal const int RetirementLimit = 2 * ActiveLimit;

        private sealed record Identifier(ulong Sequence, byte[] Id, byte[] Token);

        private readonly List<Identifier> _active = [new(0, CopyInitial(initial), [])];
        private readonly Dictionary<ulong, Delivery> _retiring = [];
        private readonly Func<ulong, Delivery> _retire = retire ?? throw new ArgumentNullException(nameof(retire));
        private readonly bool _zeroLength = initial.Length == 0;
        private ulong _retirePrior;
        private bool _disposed;
        private ulong? _usedSequence = initialUsed ? 0UL : null;
        private byte[]? _snapshotSource;
        private ReadOnlyMemory<byte> _resetSnapshot;

        internal int ActiveCount => _active.Count;
        internal int RetainedRetirements => _retiring.Count;
        internal ulong RetirePrior => _retirePrior;

        internal ReadOnlySpan<byte> Current
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _active[0].Id;
            }
        }

        // Snapshots are immutable publication metadata; copying is limited to a
        // selected token change, never ordinary packets. Unused/retired IDs do not qualify.
        internal ReadOnlyMemory<byte> ResetToken
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var current = _active[0];

                if (_usedSequence != current.Sequence || current.Token.Length != 16) return default;

                if (!ReferenceEquals(_snapshotSource, current.Token))
                {
                    _snapshotSource = current.Token;
                    byte[] copy = [.. current.Token];
                    _resetSnapshot = copy;
                }

                return _resetSnapshot;
            }
        }

        internal void MarkUsed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _usedSequence = _active[0].Sequence;
        }

        internal bool MatchesReset(ReadOnlySpan<byte> datagram)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _active[0];
            return datagram.Length >= 21
                && _usedSequence == current.Sequence
                && current.Token.Length == 16
                && CryptographicOperations.FixedTimeEquals(datagram[^16..], current.Token);
        }

        private static byte[] CopyInitial(byte[] initial)
        {
            ArgumentNullException.ThrowIfNull(initial);

            if (initial.Length > 20) throw new ArgumentOutOfRangeException(nameof(initial));

            return [.. initial];
        }

        internal void Add(ulong sequence, ulong retirePrior, ReadOnlySpan<byte> id, ReadOnlySpan<byte> token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (
                sequence > PacketNumberWindow.Maximum
                || retirePrior > sequence
                || id.Length is < 1 or > 20 || token.Length != 16
            )
                throw new TransportException(7, "Invalid peer connection-ID frame.");

            if (_zeroLength) throw new TransportException(10, "A zero-length peer ID cannot be replaced.");

            Prune();
            Identifier? existing = null;

            foreach (var active in _active)
            {
                if (active.Sequence == sequence)
                {
                    if (
                        !id.SequenceEqual(active.Id)
                        || (active.Token.Length != 0 && !CryptographicOperations.FixedTimeEquals(token, active.Token))
                    )
                        throw new TransportException(10, "Conflicting peer connection-ID sequence.");

                    existing = active;
                }

                else if (
                    id.SequenceEqual(active.Id)
                    || (active.Token.Length != 0 && CryptographicOperations.FixedTimeEquals(token, active.Token))
                )
                    throw new TransportException(10, "Repeated peer connection ID or reset token.");
            }

            _retirePrior = Math.Max(_retirePrior, retirePrior);
            Span<ulong> retired = stackalloc ulong[ActiveLimit + 1];
            var count = 0;

            for (var index = _active.Count - 1; index >= 0; index--)
            {
                var active = _active[index];

                if (active.Sequence >= _retirePrior) continue;

                retired[count++] = active.Sequence;
                CryptographicOperations.ZeroMemory(active.Token);
                _active.RemoveAt(index);
            }

            if (sequence < _retirePrior) retired[count++] = sequence;

            else if (existing == null)
            {
                if (_active.Count >= ActiveLimit)
                    throw new TransportException(9, "Peer active connection-ID limit exceeded.");

                _active.Add(new Identifier(sequence, id.ToArray(), token.ToArray()));
            }

            else if (existing.Token.Length == 0)
                _active[_active.IndexOf(existing)] = existing with { Token = token.ToArray() };

            for (var index = 0; index < count; index++)
            {
                var value = retired[index];

                if (_retiring.ContainsKey(value)) continue;

                if (_retiring.Count >= RetirementLimit)
                    throw new TransportException(9, "Peer connection-ID retirement limit exceeded.");

                _retiring.Add(value, _retire(value));
            }
        }

        internal void Prune()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_retiring.Count == 0) return;

            Span<ulong> acknowledged = stackalloc ulong[RetirementLimit];
            var count = 0;

            foreach (var (sequence, delivery) in _retiring)
                if (delivery.Acknowledged) acknowledged[count++] = sequence;

            for (var index = 0; index < count; index++) _retiring.Remove(acknowledged[index]);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            foreach (var active in _active) CryptographicOperations.ZeroMemory(active.Token);

            _active.Clear();
            _retiring.Clear();
            _snapshotSource = null;
            _resetSnapshot = default;
        }
    }
}
