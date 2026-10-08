using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmbedIO.Net.Internal.Http2
{
    internal readonly struct HpackField
    {
        internal HpackField(string name, string value, bool neverIndexed = false)
        { Name = name; Value = value; NeverIndexed = neverIndexed; }
        public string Name { get; }
        public string Value { get; }
        public bool NeverIndexed { get; }
        internal int Size => checked(Name.Length + Value.Length + 32);
    }

    internal static class HpackStaticTable
    {
        // RFC 7541 Appendix A. Copyright (c) 2015 IETF Trust and the persons
        // identified as the document authors; see the code-components LICENSE notice.
        internal static readonly HpackField[] Entries = { new HpackField(":authority", ""),
            new HpackField(":method", "GET"),
            new HpackField(":method", "POST"),
            new HpackField(":path", "/"),
            new HpackField(":path", "/index.html"),
            new HpackField(":scheme", "http"),
            new HpackField(":scheme", "https"),
            new HpackField(":status", "200"),
            new HpackField(":status", "204"),
            new HpackField(":status", "206"),
            new HpackField(":status", "304"),
            new HpackField(":status", "400"),
            new HpackField(":status", "404"),
            new HpackField(":status", "500"),
            new HpackField("accept-charset", ""),
            new HpackField("accept-encoding", "gzip, deflate"),
            new HpackField("accept-language", ""),
            new HpackField("accept-ranges", ""),
            new HpackField("accept", ""),
            new HpackField("access-control-allow-origin", ""),
            new HpackField("age", ""),
            new HpackField("allow", ""),
            new HpackField("authorization", ""),
            new HpackField("cache-control", ""),
            new HpackField("content-disposition", ""),
            new HpackField("content-encoding", ""),
            new HpackField("content-language", ""),
            new HpackField("content-length", ""),
            new HpackField("content-location", ""),
            new HpackField("content-range", ""),
            new HpackField("content-type", ""),
            new HpackField("cookie", ""),
            new HpackField("date", ""),
            new HpackField("etag", ""),
            new HpackField("expect", ""),
            new HpackField("expires", ""),
            new HpackField("from", ""),
            new HpackField("host", ""),
            new HpackField("if-match", ""),
            new HpackField("if-modified-since", ""),
            new HpackField("if-none-match", ""),
            new HpackField("if-range", ""),
            new HpackField("if-unmodified-since", ""),
            new HpackField("last-modified", ""),
            new HpackField("link", ""),
            new HpackField("location", ""),
            new HpackField("max-forwards", ""),
            new HpackField("proxy-authenticate", ""),
            new HpackField("proxy-authorization", ""),
            new HpackField("range", ""),
            new HpackField("referer", ""),
            new HpackField("refresh", ""),
            new HpackField("retry-after", ""),
            new HpackField("server", ""),
            new HpackField("set-cookie", ""),
            new HpackField("strict-transport-security", ""),
            new HpackField("transfer-encoding", ""),
            new HpackField("user-agent", ""),
            new HpackField("vary", ""),
            new HpackField("via", ""),
            new HpackField("www-authenticate", "") };
    }

    // One decoder per connection direction. Complete HEADERS/CONTINUATION blocks
    // must arrive in wire order. Any decoding/resource failure poisons this state;
    // the caller must terminate that connection, never reuse a partial table.
    internal sealed class HpackDecoder
    {
        private static readonly Encoding Octets = Encoding.GetEncoding(28591);
        private readonly List<HpackField> _dynamic = new();
        private readonly int _maximumHeaderListSize;
        private int _maximumTableSize;
        private int _capacity;
        private int _tableBytes;
        private int _requiredMinimum = int.MaxValue;
        private bool _failed;

        internal HpackDecoder(int maximumHeaderListSize = 32768)
        {
            if (maximumHeaderListSize < 0) throw new ArgumentOutOfRangeException(nameof(maximumHeaderListSize));
            _maximumHeaderListSize = maximumHeaderListSize;
            _maximumTableSize = _capacity = 4096;
        }

        // Invoke when the peer has acknowledged our SETTINGS_HEADER_TABLE_SIZE.
        // Multiple changes must preserve the smallest required eviction point.
        internal void SetMaximumTableSize(int size)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            _maximumTableSize = size;
            if (size < _capacity) _requiredMinimum = Math.Min(_requiredMinimum, size);
        }

        internal HpackField[] Decode(byte[] block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            if (_failed) throw new InvalidDataException("HPACK decoder is no longer usable.");
            try
            {
                var headers = new List<HpackField>();
                var position = 0;
                var remaining = _maximumHeaderListSize;
                var sawHeader = false;
                while (position < block.Length)
                {
                    var first = block[position];
                    if ((first & 0xe0) == 0x20)
                    {
                        if (sawHeader) throw new InvalidDataException("HPACK table update follows a field.");
                        var size = PrefixInteger.Read(block, ref position, block.Length, 5);
                        if (size > _maximumTableSize || size > _requiredMinimum)
                            throw new InvalidDataException("HPACK table update exceeds negotiated size.");
                        _requiredMinimum = int.MaxValue;
                        _capacity = size;
                        Evict(0);
                        continue;
                    }
                    if (_requiredMinimum != int.MaxValue)
                        throw new InvalidDataException("Missing required HPACK table size update.");
                    sawHeader = true;
                    HpackField field;
                    if ((first & 128) != 0)
                        field = Get(PrefixInteger.Read(block, ref position, block.Length, 7));
                    else
                    {
                        var indexed = (first & 64) != 0;
                        var nameIndex = PrefixInteger.Read(block, ref position, block.Length, indexed ? 6 : 4);
                        var allowance = remaining - 32;
                        if (allowance < 0) throw new InvalidDataException("HPACK header list exceeds limit.");
                        var name = nameIndex == 0 ? ReadString(block, ref position, allowance) : Get(nameIndex).Name;
                        var value = ReadString(block, ref position, allowance - name.Length);
                        field = new HpackField(name, value, !indexed && (first & 16) != 0);
                        if (indexed) Insert(field);
                    }
                    if (field.Size > remaining) throw new InvalidDataException("HPACK header list exceeds limit.");
                    remaining -= field.Size;
                    headers.Add(field);
                }
                if (_requiredMinimum != int.MaxValue)
                    throw new InvalidDataException("Missing required HPACK table size update.");
                return headers.ToArray();
            }
            catch { _failed = true; throw; }
        }

        private static string ReadString(byte[] block, ref int position, int maximum)
        {
            if (maximum < 0) throw new InvalidDataException("HPACK header list exceeds limit.");
            if (position == block.Length) throw new EndOfStreamException("Missing HPACK string.");
            var huffman = (block[position] & 128) != 0;
            var length = PrefixInteger.Read(block, ref position, block.Length, 7);
            if (length > block.Length - position) throw new EndOfStreamException("Incomplete HPACK string.");
            string result;
            if (huffman) result = HpackHuffman.Decode(block, position, length, maximum);
            else
            {
                if (length > maximum) throw new InvalidDataException("HPACK header list exceeds limit.");
                result = Octets.GetString(block, position, length);
            }
            position += length;
            return result;
        }

        private HpackField Get(int index)
        {
            if (index <= 0) throw new InvalidDataException("HPACK index zero is invalid.");
            if (index <= HpackStaticTable.Entries.Length) return HpackStaticTable.Entries[index - 1];
            index -= HpackStaticTable.Entries.Length + 1;
            if (index >= _dynamic.Count) throw new InvalidDataException("HPACK index is outside the table.");
            return _dynamic[index];
        }

        private void Insert(HpackField field)
        {
            if (field.Size > _capacity) { _dynamic.Clear(); _tableBytes = 0; return; }
            Evict(field.Size);
            _dynamic.Insert(0, field);
            _tableBytes += field.Size;
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
