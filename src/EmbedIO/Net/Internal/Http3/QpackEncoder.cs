using System;
using System.Collections.Generic;
using System.IO;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    // A stateless encoder is valid at any peer table capacity and cannot block a
    // peer stream. Dynamic response compression can be added independently.
    internal static partial class QpackEncoder
    {
        private static readonly Dictionary<(string Name, string Value), int> Exact = new();
        private static readonly Dictionary<string, int> Names = new(StringComparer.Ordinal);
        static QpackEncoder()
        {
            for (var i = 0; i < QpackStaticTable.Entries.Length; i++)
            {
                var field = QpackStaticTable.Entries[i];
                Exact[(field.Name, field.Value)] = i;
                if (!Names.ContainsKey(field.Name)) Names.Add(field.Name, i);
            }
        }

        internal static byte[] Encode(HpackField[] fields, int maximumEncodedBytes, int maximumDecodedBytes)
        {
            Validate(fields, maximumEncodedBytes, maximumDecodedBytes);
            using var output = new MemoryStream(Math.Min(maximumEncodedBytes, 256));
            output.WriteByte(0);
            output.WriteByte(0);
            foreach (var field in fields) WriteField(output, field, maximumEncodedBytes);
            return output.ToArray();
        }
        private static void Validate(HpackField[] fields, int maximumEncodedBytes, int maximumDecodedBytes)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            if (maximumEncodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
            if (maximumDecodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
            if (maximumEncodedBytes < 2) throw Limit();
            long total = 0;
            foreach (var field in fields)
            {
                if (field.Name == null || field.Value == null) throw new ArgumentException("Missing QPACK field data.", nameof(fields));
                total += (long)field.Name.Length + field.Value.Length + 32;
                if (total > maximumDecodedBytes) throw Limit();
            }
        }
        private static void WriteField(MemoryStream output, HpackField field, int maximumEncodedBytes)
        {
            var sensitive = field.NeverIndexed || Sensitive(field.Name);
            if (!sensitive && Exact.TryGetValue((field.Name, field.Value), out var exact))
            {
                Ensure(output, IntegerLength(exact, 6), maximumEncodedBytes);
                QpackInteger.Write(output, exact, 6, 192);
                return;
            }
            var value = new StringEncoding(field.Value);
            if (Names.TryGetValue(field.Name, out var name))
            {
                Ensure(output, IntegerLength(name, 4) + value.Size(7), maximumEncodedBytes);
                QpackInteger.Write(output, name, 4, (byte)(80 | (sensitive ? 32 : 0)));
            }
            else
            {
                var literalName = new StringEncoding(field.Name);
                Ensure(output, literalName.Size(3) + value.Size(7), maximumEncodedBytes);
                literalName.Write(output, 3, 8, (byte)(32 | (sensitive ? 16 : 0)));
            }
            value.Write(output, 7, 128, 0);
        }
        internal static bool TryWriteInsert(MemoryStream output, HpackField field, int maximum)
        {
            var value = new StringEncoding(field.Value);
            if (Names.TryGetValue(field.Name, out var name))
            {
                if (IntegerLength(name, 6) + value.Size(7) > maximum - output.Length) return false;
                QpackInteger.Write(output, name, 6, 192);
            }
            else
            {
                var literal = new StringEncoding(field.Name);
                if (literal.Size(5) + value.Size(7) > maximum - output.Length) return false;
                literal.Write(output, 5, 32, 64);
            }
            value.Write(output, 7, 128, 0);
            return true;
        }
        internal static bool Sensitive(string name) => name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase);
        private static void Ensure(MemoryStream output, long additional, int limit)
        { if (additional > limit - output.Length) throw Limit(); }
        private static int IntegerLength(long value, int bits)
        {
            var mask = (1 << bits) - 1;
            if (value < mask) return 1;
            var length = 2;
            for (value -= mask; value >= 128; value >>= 7) length++;
            return length;
        }
        private static InvalidDataException Limit() => new("QPACK output exceeds configured field section limit.");
        private readonly struct StringEncoding
        {
            private readonly string _value;
            private readonly bool _huffman;
            private readonly int _length;
            internal StringEncoding(string value)
            {
                _value = value;
                var compressed = HpackHuffman.EncodedLength(value);
                _huffman = compressed < value.Length;
                _length = _huffman ? compressed : value.Length;
            }
            internal long Size(int bits) => (long)_length + IntegerLength(_length, bits);
            internal void Write(Stream output, int bits, byte huffmanFlag, byte flags)
            {
                QpackInteger.Write(output, _length, bits, (byte)(flags | (_huffman ? huffmanFlag : 0)));
                if (_huffman) HpackHuffman.EncodeString(output, _value);
                else foreach (var octet in _value) output.WriteByte((byte)octet);
            }
        }
    }
}
