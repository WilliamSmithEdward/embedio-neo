using System;
using System.Collections.Generic;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    // The connection gate serializes planning, pinning, queueing and draining.
    // Feedback may concurrently advance acknowledgment and release references.
    internal sealed class QpackResponseEncoder
    {
        private readonly QpackEncoderFeedback _feedback;
        private readonly int _maximumCapacity;
        private readonly int _maximumPendingBytes;
        private readonly Queue<byte[]> _instructions = new();
        private QpackEncoderTable? _table;
        private long _peerCapacity;
        private int _pendingBytes;
        private ulong[]? _recent;
        private ulong _occupied;

        internal QpackResponseEncoder(QpackEncoderFeedback feedback, int maximumCapacity, int maximumPendingBytes)
        {
            _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
            if (maximumCapacity < 0) throw new ArgumentOutOfRangeException(nameof(maximumCapacity));
            if (maximumPendingBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumPendingBytes));
            _maximumCapacity = maximumCapacity;
            _maximumPendingBytes = maximumPendingBytes;
        }
        internal int PendingBytes => _pendingBytes;
        internal byte[] Encode(long streamId, HpackField[] fields, Http3PeerSettings peer, int maximumEncodedBytes, int maximumDecodedBytes)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            if (peer.MaximumTableCapacity == 0 || _maximumCapacity == 0)
                return QpackEncoder.Encode(fields, maximumEncodedBytes, maximumDecodedBytes);
            if (_table == null)
            {
                _peerCapacity = peer.MaximumTableCapacity;
                _table = new QpackEncoderTable((int)Math.Min(_maximumCapacity, _peerCapacity), _feedback);
            }
            if (_peerCapacity != peer.MaximumTableCapacity) throw new InvalidOperationException("QPACK peer capacity changed.");
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            long[]? indices = null;
            var known = _feedback.KnownReceivedCount;
            if (known != 0)
            {
                for (var i = 0; i < fields.Length; ++i)
                {
                    var field = fields[i];
                    // Serialization below still validates every field and limit.
                    // Static and sensitive fields are never stored in this table.
                    if (field.Name == null || field.Value == null || field.NeverIndexed) continue;
                    var index = _table.Find(field.Name, field.Value);
                    // Pending encoder bytes never hold a response hostage to
                    // encoder-stream credit. Inserts benefit later responses.
                    if (index < 0 || index >= known) continue;
                    if (indices == null)
                    {
                        indices = new long[fields.Length];
                        for (var j = 0; j < indices.Length; ++j) indices[j] = -1;
                    }
                    indices[i] = index;
                }
            }
            byte[] wire;
            if (indices == null) wire = QpackEncoder.Encode(fields, maximumEncodedBytes, maximumDecodedBytes);
            else
            {
                var section = QpackEncoder.EncodeReferenced(fields, indices, _peerCapacity, maximumEncodedBytes, maximumDecodedBytes);
                // This private serializer result is never exposed to callers.
                // Transfer its reference array rather than copying it again.
                wire = _feedback.TryRegisterOwnedSection(streamId, section.References, peer.BlockedStreams)
                    ? section.Wire : QpackEncoder.Encode(fields, maximumEncodedBytes, maximumDecodedBytes);
            }
            // Pin selected entries before considering an insertion that could
            // otherwise evict an entry referenced by this very response.
            for (var i = 0; i < fields.Length; ++i)
            {
                var field = fields[i];
                if ((indices != null && indices[i] >= 0) || QpackEncoder.IsStatic(field)) continue;
                if (!Admit(field)) continue;
                var insertion = _table.TryInsert(field, _maximumPendingBytes - _pendingBytes);
                if (insertion == null) continue;
                _instructions.Enqueue(insertion.Instructions);
                _pendingBytes += insertion.Instructions.Length;
            }
            return wire;
        }
        private bool Admit(HpackField field)
        {
            var table = _table ?? throw new InvalidOperationException("Missing QPACK table.");
            var capacity = Math.Min(_maximumCapacity, _peerCapacity);
            var size = (long)field.Name.Length + field.Value.Length + 32;
            if (field.NeverIndexed || QpackEncoder.Sensitive(field.Name) || size > capacity) return false;
            if (size <= capacity - table.StoredBytes) return true;
            if (table.Find(field.Name, field.Value) >= 0) return false;
            // Under table pressure, require a recent repeat before paying for
            // an eviction and insertion. Store only bounded fingerprints, never
            // another copy of application headers. Collisions affect compression
            // choices only; actual table lookups and wire values remain exact.
            var fingerprint = 14695981039346656037UL;
            foreach (var octet in field.Name) fingerprint = unchecked((fingerprint ^ octet) * 1099511628211UL);
            fingerprint = unchecked((fingerprint ^ 65536) * 1099511628211UL);
            foreach (var octet in field.Value) fingerprint = unchecked((fingerprint ^ octet) * 1099511628211UL);
            var slot = (int)((fingerprint ^ (fingerprint >> 32)) & 63);
            var bit = 1UL << slot;
            _recent ??= new ulong[64];
            var repeated = (_occupied & bit) != 0 && _recent[slot] == fingerprint;
            _recent[slot] = fingerprint;
            _occupied |= bit;
            return repeated;
        }
        internal byte[]? DequeueInstructions()
        {
            if (_instructions.Count == 0) return null;
            var bytes = _instructions.Dequeue();
            _pendingBytes -= bytes.Length;
            return bytes;
        }
        internal void Clear()
        {
            _instructions.Clear();
            _pendingBytes = 0;
            _table = null;
            _recent = null;
            _occupied = 0;
        }
    }
}
