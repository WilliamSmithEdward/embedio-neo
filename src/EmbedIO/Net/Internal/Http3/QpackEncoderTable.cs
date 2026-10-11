using System;
using System.Collections.Generic;
using System.IO;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    // A connection's encoder gate serializes insertions, lookups and section
    // registration. Decoder feedback can run concurrently: it only releases
    // references and advances the acknowledged insertion count.
    internal sealed class QpackEncoderTable
    {
        internal sealed class Insertion
        {
            internal Insertion(long index, byte[] instructions) { Index = index; Instructions = instructions; }
            public long Index { get; }
            public byte[] Instructions { get; }
        }
        private readonly int _capacity;
        private readonly QpackEncoderFeedback _feedback;
        private readonly Queue<(long Index, HpackField Field)> _entries = new();
        private readonly Dictionary<(string Name, string Value), long> _exact = new();
        private int _bytes;
        private bool _initialized;

        internal QpackEncoderTable(int capacity, QpackEncoderFeedback feedback)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        }
        internal int StoredBytes => _bytes;
        internal int Count => _entries.Count;
        internal long Find(string name, string value) => _exact.TryGetValue((name, value), out var index) ? index : -1;

        internal Insertion? TryInsert(HpackField field, int maximumInstructionBytes)
        {
            if (maximumInstructionBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumInstructionBytes));
            if (field.Name == null || field.Value == null) throw new ArgumentException("Missing QPACK field data.", nameof(field));
            var size = (long)field.Name.Length + field.Value.Length + 32;
            if (field.NeverIndexed || QpackEncoder.Sensitive(field.Name) || size > _capacity || Find(field.Name, field.Value) >= 0) return null;
            var evictions = 0;
            var available = _capacity - _bytes;
            foreach (var entry in _entries)
            {
                if (available >= size) break;
                if (entry.Index >= _feedback.KnownReceivedCount || _feedback.IsReferenced(entry.Index)) return null;
                available += entry.Field.Size;
                ++evictions;
            }
            // Encode and validate before mutating either table or feedback state.
            // A refused instruction must not consume an absolute index or evict.
            using var output = new MemoryStream();
            if (!_initialized) QpackInteger.Write(output, _capacity, 5, 32);
            if (!QpackEncoder.TryWriteInsert(output, field, maximumInstructionBytes)) return null;
            var wire = output.ToArray();
            var index = _feedback.RegisterInsert();
            while (evictions-- > 0)
            {
                var entry = _entries.Dequeue();
                _bytes -= entry.Field.Size;
                _exact.Remove((entry.Field.Name, entry.Field.Value));
            }
            _entries.Enqueue((index, field));
            _exact.Add((field.Name, field.Value), index);
            _bytes += (int)size;
            _initialized = true;
            return new Insertion(index, wire);
        }
    }
}
