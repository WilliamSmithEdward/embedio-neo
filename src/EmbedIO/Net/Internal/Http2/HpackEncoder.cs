using System;
using System.Collections.Generic;
using System.IO;

namespace EmbedIO.Net.Internal.Http2
{
    // One encoder per connection direction, serialized in header-block wire order.
    internal sealed class HpackEncoder
    {
        private readonly List<HpackField> _dynamic = new();
        private readonly int _maximumHeaderListSize;
        private int _capacity = 4096;
        private int _tableBytes;
        private int _pendingMinimum = int.MaxValue;
        private int _pendingFinal;

        internal HpackEncoder(int maximumHeaderListSize = 32768)
        {
            if (maximumHeaderListSize < 0) throw new ArgumentOutOfRangeException(nameof(maximumHeaderListSize));
            _maximumHeaderListSize = maximumHeaderListSize;
        }

        internal void SetMaximumTableSize(int size)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            if (size == _capacity) return;
            _capacity = size;
            _pendingMinimum = Math.Min(_pendingMinimum, size);
            _pendingFinal = size;
            Evict(0);
        }

        internal byte[] Encode(HpackField[] fields)
        {
            using var output = new MemoryStream();
            EncodeTo(fields, output);
            return output.ToArray();
        }

        // Appends one header block. Validation precedes any table change.
        internal void EncodeTo(HpackField[] fields, Stream output)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            if (output == null) throw new ArgumentNullException(nameof(output));
            // Invalid application data must not partially mutate compression state.
            long total = 0;
            foreach (var field in fields)
            {
                if (field.Name == null || field.Value == null) throw new ArgumentException("Missing HPACK field data.", nameof(fields));
                total += (long)field.Name.Length + field.Value.Length + 32;
                if (total > _maximumHeaderListSize) throw new InvalidDataException("HPACK header list exceeds limit.");
                ValidateOctets(field.Name);
                ValidateOctets(field.Value);
            }
            if (_pendingMinimum != int.MaxValue)
            {
                PrefixInteger.Write(output, _pendingMinimum, 5, 0x20);
                if (_pendingFinal != _pendingMinimum) PrefixInteger.Write(output, _pendingFinal, 5, 0x20);
                _pendingMinimum = int.MaxValue;
            }
            foreach (var field in fields)
            {
                var sensitive = field.NeverIndexed || IsSensitive(field.Name);
                Find(field, out var exact, out var name);
                if (!sensitive && exact != 0)
                {
                    PrefixInteger.Write(output, exact, 7, 0x80);
                    continue;
                }
                var index = !sensitive && field.Size <= _capacity;
                PrefixInteger.Write(output, name, index ? 6 : 4, sensitive ? (byte)0x10 : index ? (byte)0x40 : (byte)0);
                if (name == 0) WriteString(output, field.Name);
                WriteString(output, field.Value);
                if (index)
                {
                    Evict(field.Size);
                    _dynamic.Insert(0, new HpackField(field.Name, field.Value));
                    _tableBytes += field.Size;
                }
            }
        }

        private static bool IsSensitive(string name) => name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase);

        private static void ValidateOctets(string value)
        {
            foreach (var c in value)
                if (c > 255) throw new ArgumentException("HPACK fields must contain octets.", nameof(value));
        }

        private static void WriteString(Stream output, string value)
        {
            var encodedLength = HpackHuffman.EncodedLength(value);
            var huffman = encodedLength < value.Length;
            PrefixInteger.Write(output, huffman ? encodedLength : value.Length, 7, huffman ? (byte)0x80 : (byte)0);
            if (huffman) HpackHuffman.EncodeString(output, value);
            else foreach (var c in value) output.WriteByte((byte)c);
        }

        private void Find(HpackField field, out int exact, out int name)
        {
            exact = name = 0;
            for (var index = 0; index < HpackStaticTable.Entries.Length; index++)
            {
                var item = HpackStaticTable.Entries[index];
                if (item.Name != field.Name) continue;
                if (name == 0) name = index + 1;
                if (item.Value == field.Value) { exact = index + 1; return; }
            }
            for (var index = 0; index < _dynamic.Count; index++)
            {
                var item = _dynamic[index];
                if (item.Name != field.Name) continue;
                if (name == 0) name = HpackStaticTable.Entries.Length + index + 1;
                if (item.Value == field.Value) { exact = HpackStaticTable.Entries.Length + index + 1; return; }
            }
        }

        private void Evict(int needed)
        {
            while (_tableBytes > _capacity - needed)
            {
                var index = _dynamic.Count - 1;
                _tableBytes -= _dynamic[index].Size;
                _dynamic.RemoveAt(index);
            }
        }
    }
}
