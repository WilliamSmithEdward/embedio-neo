using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    internal sealed class QpackFieldSectionLimitException : Http3ProtocolException
    {
        internal QpackFieldSectionLimitException(string message) : base(0x107, message) { }
    }

    // Owns one bounded HEADERS payload. Resolve its wrapped prefix exactly once:
    // re-reading it after inserts arrive could resolve it to a different generation.
    internal sealed class QpackFieldSection
    {
        private static readonly Encoding Octets = Encoding.GetEncoding(28591);
        private readonly QpackDecoderTable _table;
        private readonly byte[] _wire;
        private readonly int _fieldsOffset;
        private readonly int _maximumDecodedBytes;
        private readonly QpackSectionPrefix _prefix;

        internal QpackFieldSection(QpackDecoderTable table, byte[] wire, int maximumEncodedBytes,
            int maximumDecodedBytes, int maximumCapacity, long insertCount)
        {
            if (wire == null) throw new ArgumentNullException(nameof(wire));
            if (maximumEncodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
            if (maximumDecodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
            if (wire.Length > maximumEncodedBytes) throw new QpackFieldSectionLimitException("QPACK field section exceeds encoded limit.");
            _table = table;
            _maximumDecodedBytes = maximumDecodedBytes;
            var offset = 0;
            _prefix = QpackSectionPrefix.Read(wire, ref offset, wire.Length, maximumCapacity, insertCount);
            _fieldsOffset = offset;
            _wire = (byte[])wire.Clone();
        }
        public long RequiredInsertCount => _prefix.RequiredInsertCount;
        public long Base => _prefix.Base;
        // Null means blocked; connection coordination must enforce the blocked-stream
        // limit and retain flow-control credit until the section is consumed/canceled.
        internal HpackField[]? TryDecode() => _table.DecodeSection(this);

        // Decoding is synchronous, so each thread reuses one scratch list and only
        // the exact result array is allocated. Cleared entries release strings.
        [ThreadStatic] private static List<HpackField>? t_fields;
        // Called while the table lock is held, so eviction cannot interleave with decoding.
        internal HpackField[] DecodeEntries()
        {
            var fields = t_fields ?? new List<HpackField>(16);
            t_fields = null;
            try { return DecodeEntries(fields); }
            finally { fields.Clear(); if (fields.Capacity <= 64) t_fields = fields; }
        }
        private HpackField[] DecodeEntries(List<HpackField> fields)
        {
            var cursor = _fieldsOffset;
            var remaining = _maximumDecodedBytes;
            var largest = -1L;
            try
            {
                while (cursor < _wire.Length)
                {
                    if (remaining < 32) throw Limit();
                    var first = _wire[cursor];
                    HpackField field;
                    if ((first & 128) != 0)
                    {
                        var index = Integer(ref cursor, 6);
                        field = (first & 64) != 0 ? Static(index) : Dynamic(index, false, ref largest);
                    }
                    else if ((first & 64) != 0)
                    {
                        var index = Integer(ref cursor, 4);
                        var name = ((first & 16) != 0 ? Static(index) : Dynamic(index, false, ref largest)).Name;
                        field = new HpackField(name, Literal(ref cursor, 7, 128, remaining - 32 - name.Length), (first & 32) != 0);
                    }
                    else if ((first & 32) != 0)
                    {
                        var name = Literal(ref cursor, 3, 8, remaining - 32);
                        field = new HpackField(name, Literal(ref cursor, 7, 128, remaining - 32 - name.Length), (first & 16) != 0);
                    }
                    else if ((first & 16) != 0)
                    {
                        field = Dynamic(Integer(ref cursor, 4), true, ref largest);
                    }
                    else
                    {
                        var name = Dynamic(Integer(ref cursor, 3), true, ref largest).Name;
                        field = new HpackField(name, Literal(ref cursor, 7, 128, remaining - 32 - name.Length), (first & 8) != 0);
                    }
                    var size = (long)field.Name.Length + field.Value.Length + 32;
                    if (size > remaining) throw Limit();
                    remaining -= (int)size;
                    fields.Add(field);
                }
            }
            catch (EndOfStreamException) { throw Invalid("Truncated QPACK field representation."); }
            catch (InvalidDataException) { throw Invalid("Invalid QPACK literal or integer."); }
            if (largest + 1 != RequiredInsertCount) throw Invalid("Required Insert Count does not match field references.");
            return fields.ToArray();
        }
        private long Integer(ref int cursor, int bits) => QpackInteger.Read(_wire, ref cursor, _wire.Length, bits);
        private string Literal(ref int cursor, int bits, int flag, int maximum)
        {
            if (maximum < 0) throw Limit();
            if (cursor == _wire.Length) throw new EndOfStreamException();
            var huffman = (_wire[cursor] & flag) != 0;
            var length = Integer(ref cursor, bits);
            if (length > _wire.Length - cursor) throw new EndOfStreamException();
            string result;
            if (huffman) result = HpackHuffman.DecodeBounded(_wire, cursor, (int)length, maximum, Limit);
            else
            {
                if (length > maximum) throw Limit();
                result = Octets.GetString(_wire, cursor, (int)length);
            }
            cursor += (int)length;
            return result;
        }
        private HpackField Dynamic(long index, bool postBase, ref long largest)
        {
            long absolute;
            if (postBase)
            {
                if (index > QuicInteger.Maximum - Base) throw Invalid("QPACK post-Base index overflows.");
                absolute = Base + index;
            }
            else
            {
                if (index >= Base) throw Invalid("QPACK relative index precedes table origin.");
                absolute = Base - index - 1;
            }
            if (absolute >= RequiredInsertCount) throw Invalid("QPACK reference exceeds Required Insert Count.");
            var field = _table.GetAbsolute(absolute);
            largest = Math.Max(largest, absolute);
            return field;
        }
        private static HpackField Static(long index)
        {
            if (index >= QpackStaticTable.Entries.Length) throw Invalid("Invalid QPACK static field index.");
            return QpackStaticTable.Entries[(int)index];
        }
        private static Http3ProtocolException Invalid(string message) => new(0x200, message);
        private static QpackFieldSectionLimitException Limit() => new("QPACK decoded field section exceeds limit.");
    }
}
