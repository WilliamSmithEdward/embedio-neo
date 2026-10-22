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
        private ulong _locallyRetiredBefore;
        private bool _disposed;
        private readonly HashSet<ulong> _used = initialUsed ? [0] : [];
        private readonly HashSet<ulong> _assigned = initialUsed ? [0] : [];
        private IReadOnlyList<ReadOnlyMemory<byte>>? _tokens;
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
                return _active.Count == 0 ? [] : _active[0].Id;
            }
        }

        // Snapshots are immutable publication metadata; copying is limited to a
        // selected token change, never ordinary packets. Unused/retired IDs do not qualify.
        internal ReadOnlyMemory<byte> ResetToken
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_active.Count == 0) return default;

                var current = _active[0];

                if (!_used.Contains(current.Sequence) || current.Token.Length != 16) return default;

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

            if (_active.Count != 0) MarkUsed(_active[0].Sequence);
        }

        internal void MarkUsed(ulong sequence)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _assigned.Add(sequence);

            if (_used.Add(sequence)) _tokens = null;
        }

        internal IReadOnlyList<ReadOnlyMemory<byte>> ResetTokens
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_tokens != null) return _tokens;

                var tokens = new List<ReadOnlyMemory<byte>>(ActiveLimit);

                foreach (var value in _active)
                    if (_used.Contains(value.Sequence) && value.Token.Length == 16)
                        tokens.Add(
                            value.Token
                                .AsSpan()
                                .ToArray()
                        );

                _tokens = [.. tokens];
                return _tokens;
            }
        }

        internal bool Resolve(ulong? preferred, out ulong sequence, out ReadOnlyMemory<byte> id)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Identifier? selected = null;

            foreach (var value in _active)
                if (value.Sequence == preferred) selected = value;

            if (selected == null)
                foreach (var value in _active)
                    if (!_assigned.Contains(value.Sequence))
                    {
                        selected = value;
                        break;
                    }

            if (selected != null) _assigned.Add(selected.Sequence);

            sequence = selected?.Sequence ?? 0;
            id = selected?.Id ?? ReadOnlyMemory<byte>.Empty;
            return selected != null;
        }

        internal bool IsActive(ulong sequence)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var value in _active)
                if (value.Sequence == sequence) return true;

            return false;
        }

        internal void RetireUnused(ReadOnlySpan<ulong> sequences)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Prune();

            for (var index = _active.Count - 1; index >= 0; index--)
            {
                var value = _active[index];

                if (_active.Count == 1 || !_assigned.Contains(value.Sequence) || sequences.Contains(value.Sequence))
                    continue;

                // Keep lower active IDs as bounded exceptions. Delayed frames for
                // removed or unseen lower IDs cannot resurrect retired associations.
                _locallyRetiredBefore = Math.Max(_locallyRetiredBefore, value.Sequence + 1);
                Remove(index);
                Retire(value.Sequence);
            }
        }

        private void Remove(int index)
        {
            var value = _active[index];
            CryptographicOperations.ZeroMemory(value.Token);
            _used.Remove(value.Sequence);
            _assigned.Remove(value.Sequence);
            _active.RemoveAt(index);
            _tokens = null;
        }

        private void Retire(ulong sequence)
        {
            if (_retiring.ContainsKey(sequence)) return;

            if (_retiring.Count >= RetirementLimit)
                throw new TransportException(9, "Peer connection-ID retirement limit exceeded.");

            _retiring.Add(sequence, _retire(sequence));
        }

        internal bool MatchesReset(ReadOnlySpan<byte> datagram)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (datagram.Length < 21) return false;

            var matched = false;

            foreach (var value in _active)
                if (_used.Contains(value.Sequence) && value.Token.Length == 16)
                    matched |= CryptographicOperations.FixedTimeEquals(datagram[^16..], value.Token);

            return matched;
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
                Remove(index);
            }

            if (sequence < _retirePrior || (existing == null && sequence < _locallyRetiredBefore))
                retired[count++] = sequence;

            else if (existing == null)
            {
                if (_active.Count >= ActiveLimit)
                    throw new TransportException(9, "Peer active connection-ID limit exceeded.");

                _active.Add(new Identifier(sequence, id.ToArray(), token.ToArray()));
                _tokens = null;
            }

            else if (existing.Token.Length == 0)
            {
                _active[_active.IndexOf(existing)] = existing with { Token = token.ToArray() };
                _tokens = null;
            }

            for (var index = 0; index < count; index++)
            {
                Retire(retired[index]);
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
            _used.Clear();
            _assigned.Clear();
            _tokens = null;
        }
    }
}
