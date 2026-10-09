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
                var insertion = _table.TryInsert(field, _maximumPendingBytes - _pendingBytes);
                if (insertion == null) continue;
                _instructions.Enqueue(insertion.Instructions);
                _pendingBytes += insertion.Instructions.Length;
            }
            return wire;
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
        }
    }
}
