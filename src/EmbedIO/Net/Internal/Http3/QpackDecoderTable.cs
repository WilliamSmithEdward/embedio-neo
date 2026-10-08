using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    // Decoder-side QPACK table and incremental peer encoder instructions.
    // Field sections may read entries concurrently with the single encoder stream.
    internal sealed class QpackDecoderTable
    {
        private static readonly Encoding Octets = Encoding.GetEncoding(28591);
        private readonly object _sync = new();
        private readonly Dictionary<long, HpackField> _entries = new();
        private readonly int _maximumCapacity;
        private readonly int _maximumInstruction;
        private int _capacity;
        private int _bytes;
        private long _first;
        private long _inserts;
        private byte[] _pending = Array.Empty<byte>();
        private int _pendingCount;
        private bool _failed;

        internal QpackDecoderTable(int maximumCapacity)
        {
            if (maximumCapacity < 0 || maximumCapacity > (int.MaxValue - 32) / 4) throw new ArgumentOutOfRangeException(nameof(maximumCapacity));
            _maximumCapacity = maximumCapacity;
            // The longest HPACK Huffman code occupies 30 bits per decoded octet.
            _maximumInstruction = maximumCapacity * 4 + 32;
        }
        public long InsertCount { get { lock (_sync) return _inserts; } }
        public int StoredBytes { get { lock (_sync) return _bytes; } }
        public int Capacity { get { lock (_sync) return _capacity; } }

        internal QpackFieldSection ReadSection(byte[] wire, int maximumEncodedBytes, int maximumDecodedBytes)
        {
            lock (_sync)
            {
                if (_failed) throw new Http3ProtocolException(0x200, "QPACK table is no longer usable.");
                return new QpackFieldSection(this, wire, maximumEncodedBytes, maximumDecodedBytes, _maximumCapacity, _inserts);
            }
        }
        internal HpackField[]? DecodeSection(QpackFieldSection section)
        {
            lock (_sync)
            {
                if (_failed) throw new Http3ProtocolException(0x200, "QPACK table is no longer usable.");
                return section.RequiredInsertCount > _inserts ? null : section.DecodeEntries();
            }
        }

        internal HpackField GetAbsolute(long index)
        {
            lock (_sync)
            {
                if (_failed) throw new Http3ProtocolException(0x200, "QPACK table is no longer usable.");
                if (!_entries.TryGetValue(index, out var field)) throw new Http3ProtocolException(0x200, "Missing or evicted QPACK entry.");
                return field;
            }
        }

        internal void Feed(byte[] input, int offset, int count)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (offset < 0 || count < 0 || offset > input.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            lock (_sync)
            {
                if (_failed) throw Invalid("QPACK encoder stream already failed.");
                try
                {
                    if (_maximumCapacity == 0 && count != 0) throw Invalid("Encoder instructions forbidden with zero maximum capacity.");
                    var end = offset + count;
                    while (offset < end)
                    {
                        if (_pendingCount == 0)
                        {
                            if (TryInstruction(input, ref offset, end)) continue;
                            var tail = end - offset;
                            if (tail > _maximumInstruction) throw Invalid("QPACK instruction exceeds storage limit.");
                            EnsurePending(tail);
                            Buffer.BlockCopy(input, offset, _pending, 0, tail);
                            _pendingCount = tail;
                            return;
                        }
                        var copied = Math.Min(end - offset, _maximumInstruction - _pendingCount);
                        if (copied == 0) throw Invalid("QPACK instruction exceeds storage limit.");
                        EnsurePending(_pendingCount + copied);
                        Buffer.BlockCopy(input, offset, _pending, _pendingCount, copied);
                        _pendingCount += copied;
                        offset += copied;
                        var consumed = 0;
                        while (consumed < _pendingCount && TryInstruction(_pending, ref consumed, _pendingCount)) { }
                        if (consumed != 0)
                        {
                            var retained = _pendingCount - consumed;
                            Buffer.BlockCopy(_pending, consumed, _pending, 0, retained);
                            Array.Clear(_pending, retained, consumed);
                            _pendingCount = retained;
                        }
                    }
                }
                catch (IOException) { Fail(); throw; }
            }
        }

        // The connection separately treats closure of the critical encoder stream as H3_CLOSED_CRITICAL_STREAM.
        internal void CompleteInput()
        {
            lock (_sync)
            {
                if (_failed || _pendingCount != 0) { Fail(); throw Invalid("Truncated QPACK encoder instruction."); }
            }
        }

        private bool TryInstruction(byte[] input, ref int offset, int end)
        {
            var cursor = offset;
            try
            {
                var first = input[cursor];
                if ((first & 128) != 0)
                {
                    var index = QpackInteger.Read(input, ref cursor, end, 6);
                    var valuePlan = StringPlan.Read(input, ref cursor, end, 7, 128, _maximumInstruction);
                    var name = (first & 64) != 0 ? Static(index).Name : Relative(index).Name;
                    Insert(name, valuePlan.Decode(input, _capacity - 32 - name.Length));
                }
                else if ((first & 64) != 0)
                {
                    var namePlan = StringPlan.Read(input, ref cursor, end, 5, 32, _maximumInstruction);
                    var valuePlan = StringPlan.Read(input, ref cursor, end, 7, 128, _maximumInstruction);
                    var name = namePlan.Decode(input, _capacity - 32);
                    Insert(name, valuePlan.Decode(input, _capacity - 32 - name.Length));
                }
                else if ((first & 32) != 0)
                {
                    var capacity = QpackInteger.Read(input, ref cursor, end, 5);
                    if (capacity > _maximumCapacity) throw Invalid("QPACK capacity exceeds advertised maximum.");
                    _capacity = (int)capacity;
                    Evict(0);
                }
                else
                {
                    var index = QpackInteger.Read(input, ref cursor, end, 5);
                    var existing = Relative(index);
                    Insert(existing.Name, existing.Value);
                }
                offset = cursor;
                return true;
            }
            catch (EndOfStreamException) { return false; }
            catch (InvalidDataException) { throw Invalid("Invalid QPACK encoder literal or integer."); }
        }

        private static HpackField Static(long index)
        {
            if (index >= QpackStaticTable.Entries.Length) throw Invalid("Invalid QPACK static index.");
            return QpackStaticTable.Entries[(int)index];
        }
        private HpackField Relative(long index)
        {
            if (index >= _inserts || !_entries.TryGetValue(_inserts - index - 1, out var field)) throw Invalid("Invalid QPACK dynamic index.");
            return field;
        }
        private void Insert(string name, string value)
        {
            var size = (long)name.Length + value.Length + 32;
            if (size > _capacity || _inserts == QuicInteger.Maximum) throw Invalid("QPACK insertion exceeds capacity or counter range.");
            Evict((int)size);
            _entries.Add(_inserts++, new HpackField(name, value));
            _bytes += (int)size;
        }
        private void Evict(int needed)
        {
            while (_bytes > _capacity - needed)
            {
                var field = _entries[_first];
                _bytes -= field.Size;
                _entries.Remove(_first++);
            }
        }
        private void EnsurePending(int count)
        {
            if (_pending.Length >= count) return;
            var size = (int)Math.Min(_maximumInstruction, Math.Max(count, Math.Max(32L, _pending.Length * 2L)));
            Array.Resize(ref _pending, size);
        }
        private void Fail()
        {
            _failed = true;
            _entries.Clear();
            _bytes = 0;
            Array.Clear(_pending, 0, _pendingCount);
            _pendingCount = 0;
        }
        private static Http3ProtocolException Invalid(string message) => new(0x201, message);

        private readonly struct StringPlan
        {
            private readonly int _offset;
            private readonly int _length;
            private readonly bool _huffman;
            private StringPlan(int offset, int length, bool huffman) { _offset = offset; _length = length; _huffman = huffman; }
            internal static StringPlan Read(byte[] input, ref int offset, int end, int bits, int flag, int maximum)
            {
                if (offset == end) throw new EndOfStreamException();
                var huffman = (input[offset] & flag) != 0;
                var length = QpackInteger.Read(input, ref offset, end, bits);
                if (length > maximum) throw Invalid("QPACK encoded string exceeds limit.");
                if (length > end - offset) throw new EndOfStreamException();
                var plan = new StringPlan(offset, (int)length, huffman);
                offset += (int)length;
                return plan;
            }
            internal string Decode(byte[] input, int maximum)
            {
                if (maximum < 0) throw Invalid("QPACK insertion exceeds table capacity.");
                if (_huffman) return HpackHuffman.Decode(input, _offset, _length, maximum);
                if (_length > maximum) throw Invalid("QPACK literal exceeds table capacity.");
                return Octets.GetString(input, _offset, _length);
            }
        }
    }
}
