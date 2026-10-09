using System;
using System.IO;

namespace EmbedIO.Internal
{
    // Locates the exact RFC 1951 boundary without retaining or materializing decoded data.
    internal sealed class DeflateFramingValidator
    {
        private enum Phase { Header, StoredLength, StoredData, DynamicHeader, CodeLengths, TreeLengths, Repeat, Symbol, Length, Distance, DistanceExtra, Done }
        private static readonly int[] CodeOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
        private static readonly int[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static readonly int[] LengthBits = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static readonly int[] DistanceBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static readonly int[] DistanceBits = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static readonly Huffman FixedLiterals = FixedLiteralTree();
        private static readonly Huffman FixedDistances = new Huffman(Filled(32, 5), false, false);
        private readonly int _maximumDistance;
        internal DeflateFramingValidator() : this(32768) { }
        internal DeflateFramingValidator(int maximumDistance)
        {
            if (maximumDistance < 256 || maximumDistance > 32768) throw new ArgumentOutOfRangeException(nameof(maximumDistance));
            _maximumDistance = maximumDistance;
        }
        private Phase _phase;
        private ulong _bits;
        private int _bitCount;
        private bool _final;
        private bool _failed;
        private int _remaining;
        private int _literalCount;
        private int _distanceCount;
        private int _codeCount;
        private int _index;
        private int[]? _codeLengths;
        private int[]? _treeLengths;
        private Huffman? _codes;
        private Huffman? _literals;
        private Huffman? _distances;
        private int _repeatSymbol;
        private int _lengthIndex;
        private int _length;
        private int _distanceIndex;
        internal bool Finished => _phase == Phase.Done && !_failed;
        internal ulong DecodedBytes { get; private set; }

        internal int Feed(byte[] data, int offset, int count)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (_failed) throw new InvalidDataException("DEFLATE framing validation has failed.");
            var end = offset + count;
            var start = offset;
            try
            {
                while (_phase != Phase.Done)
                {
                    if (Advance()) continue;
                    if (offset == end) break;
                    if (_phase == Phase.StoredData)
                    {
                        var take = Math.Min(_remaining, end - offset);
                        offset += take;
                        _remaining -= take;
                        AddOutput(take);
                        if (_remaining == 0) EndBlock();
                    }
                    else
                    {
                        _bits |= (ulong)data[offset++] << _bitCount;
                        _bitCount += 8;
                    }
                }
            }
            catch (InvalidDataException) { _failed = true; throw; }
            return offset - start;
        }

        internal void Complete()
        {
            if (!Finished) { _failed = true; throw new InvalidDataException("Truncated DEFLATE stream."); }
        }

        private bool ReadBits(int count, out int value)
        {
            if (_bitCount < count) { value = 0; return false; }
            value = (int)(_bits & ((1UL << count) - 1));
            _bits >>= count;
            _bitCount -= count;
            return true;
        }

        private void AddOutput(int count)
        {
            if (DecodedBytes > ulong.MaxValue - (ulong)count) throw new InvalidDataException("DEFLATE output length overflow.");
            DecodedBytes += (ulong)count;
        }

        private void EndBlock()
        {
            _phase = _final ? Phase.Done : Phase.Header;
            if (_final) { _bits = 0; _bitCount = 0; }
        }

        private bool Advance()
        {
            int value;
            switch (_phase)
            {
                case Phase.Header:
                    if (!ReadBits(3, out value)) return false;
                    _final = (value & 1) != 0;
                    switch (value >> 1)
                    {
                        case 0:
                            var padding = _bitCount & 7;
                            _bits >>= padding;
                            _bitCount -= padding;
                            _phase = Phase.StoredLength;
                            break;
                        case 1: _literals = FixedLiterals; _distances = FixedDistances; _phase = Phase.Symbol; break;
                        case 2: _phase = Phase.DynamicHeader; break;
                        default: throw new InvalidDataException("Reserved DEFLATE block type.");
                    }
                    return true;
                case Phase.StoredLength:
                    if (!ReadBits(32, out value)) return false;
                    _remaining = value & 65535;
                    if (((value >> 16) & 65535) != (_remaining ^ 65535)) throw new InvalidDataException("Invalid DEFLATE stored block length.");
                    _phase = Phase.StoredData;
                    if (_remaining == 0) EndBlock();
                    return true;
                case Phase.StoredData: return false;
                case Phase.DynamicHeader:
                    if (!ReadBits(14, out value)) return false;
                    _literalCount = 257 + (value & 31);
                    if (_literalCount > 286) throw new InvalidDataException("Reserved DEFLATE literal count.");
                    _distanceCount = 1 + ((value >> 5) & 31);
                    _codeCount = 4 + (value >> 10);
                    _codeLengths = new int[19];
                    _treeLengths = new int[_literalCount + _distanceCount];
                    _index = 0;
                    _phase = Phase.CodeLengths;
                    return true;
                case Phase.CodeLengths:
                    if (!ReadBits(3, out value)) return false;
                    var codeLengths = _codeLengths ?? throw new InvalidOperationException("Missing code lengths.");
                    codeLengths[CodeOrder[_index++]] = value;
                    if (_index == _codeCount)
                    {
                        _codes = new Huffman(codeLengths, false, true);
                        _index = 0;
                        _phase = Phase.TreeLengths;
                    }
                    return true;
                case Phase.TreeLengths:
                    var codes = _codes ?? throw new InvalidOperationException("Missing code tree.");
                    if (!codes.Read(ref _bits, ref _bitCount, out value)) return false;
                    var treeLengths = _treeLengths ?? throw new InvalidOperationException("Missing tree lengths.");
                    if (value <= 15)
                    {
                        treeLengths[_index++] = value;
                        FinishTrees();
                    }
                    else
                    {
                        if (value == 16 && _index == 0) throw new InvalidDataException("DEFLATE length repeat lacks a previous length.");
                        _repeatSymbol = value;
                        _phase = Phase.Repeat;
                    }
                    return true;
                case Phase.Repeat:
                    if (!ReadBits(_repeatSymbol == 16 ? 2 : _repeatSymbol == 17 ? 3 : 7, out value)) return false;
                    var repeat = value + (_repeatSymbol == 18 ? 11 : 3);
                    var lengths = _treeLengths ?? throw new InvalidOperationException("Missing repeated tree lengths.");
                    if (repeat > lengths.Length - _index) throw new InvalidDataException("DEFLATE length repeat exceeds the tree.");
                    var length = _repeatSymbol == 16 ? lengths[_index - 1] : 0;
                    while (repeat-- != 0) lengths[_index++] = length;
                    _phase = Phase.TreeLengths;
                    FinishTrees();
                    return true;
                case Phase.Symbol:
                    var literals = _literals ?? throw new InvalidOperationException("Missing literal tree.");
                    if (!literals.Read(ref _bits, ref _bitCount, out value)) return false;
                    if (value < 256) AddOutput(1);
                    else if (value == 256) EndBlock();
                    else
                    {
                        if (value > 285) throw new InvalidDataException("Reserved DEFLATE length symbol.");
                        _lengthIndex = value - 257;
                        _phase = Phase.Length;
                    }
                    return true;
                case Phase.Length:
                    if (!ReadBits(LengthBits[_lengthIndex], out value)) return false;
                    _length = LengthBase[_lengthIndex] + value;
                    _phase = Phase.Distance;
                    return true;
                case Phase.Distance:
                    var distances = _distances ?? throw new InvalidOperationException("Missing distance tree.");
                    if (!distances.Read(ref _bits, ref _bitCount, out value)) return false;
                    if (value > 29) throw new InvalidDataException("Reserved DEFLATE distance symbol.");
                    _distanceIndex = value;
                    _phase = Phase.DistanceExtra;
                    return true;
                case Phase.DistanceExtra:
                    if (!ReadBits(DistanceBits[_distanceIndex], out value)) return false;
                    var distance = DistanceBase[_distanceIndex] + value;
                    if (distance > _maximumDistance || (ulong)distance > DecodedBytes) throw new InvalidDataException("DEFLATE distance precedes the output history.");
                    AddOutput(_length);
                    _phase = Phase.Symbol;
                    return true;
                default: return false;
            }
        }

        private void FinishTrees()
        {
            var lengths = _treeLengths ?? throw new InvalidOperationException("Missing tree lengths.");
            if (_index != lengths.Length) return;
            if (lengths[256] == 0) throw new InvalidDataException("DEFLATE literal tree lacks end-of-block.");
            var literalLengths = new int[_literalCount];
            var distanceLengths = new int[_distanceCount];
            Array.Copy(lengths, literalLengths, literalLengths.Length);
            Array.Copy(lengths, _literalCount, distanceLengths, 0, distanceLengths.Length);
            _literals = new Huffman(literalLengths, false, false);
            _distances = new Huffman(distanceLengths, true, false);
            _phase = Phase.Symbol;
        }

        private static int[] Filled(int length, int value)
        {
            var result = new int[length];
            for (var i = 0; i < length; i++) result[i] = value;
            return result;
        }
        private static Huffman FixedLiteralTree()
        {
            var lengths = new int[288];
            for (var i = 0; i < lengths.Length; i++) lengths[i] = i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8;
            return new Huffman(lengths, false, false);
        }

        private sealed class Huffman
        {
            private readonly int[] _left;
            private readonly int[] _right;
            private readonly int[] _symbols;
            internal Huffman(int[] lengths, bool allowEmpty, bool requireComplete)
            {
                var counts = new int[16];
                var nonzero = 0;
                var maximum = 0;
                foreach (var length in lengths)
                {
                    if (length == 0) continue;
                    counts[length]++;
                    nonzero++;
                    maximum = Math.Max(maximum, length);
                }
                if (nonzero == 0 && !allowEmpty) throw new InvalidDataException("Empty DEFLATE Huffman tree.");
                var remaining = 1;
                for (var length = 1; length <= 15; length++)
                {
                    remaining = (remaining << 1) - counts[length];
                    if (remaining < 0) throw new InvalidDataException("Oversubscribed DEFLATE Huffman tree.");
                }
                if (nonzero != 0 && remaining != 0 && (requireComplete || maximum != 1))
                    throw new InvalidDataException("Incomplete DEFLATE Huffman tree.");
                var capacity = Math.Max(2, nonzero * 2 + 1);
                _left = new int[capacity];
                _right = new int[capacity];
                _symbols = Filled(capacity, -1);
                var next = new int[16];
                var code = 0;
                for (var length = 1; length <= 15; length++) next[length] = code = (code + counts[length - 1]) << 1;
                var nodes = 1;
                for (var symbol = 0; symbol < lengths.Length; symbol++)
                {
                    var length = lengths[symbol];
                    if (length == 0) continue;
                    code = next[length]++;
                    var node = 0;
                    for (var bit = length - 1; bit >= 0; bit--)
                    {
                        var branch = ((code >> bit) & 1) == 0 ? _left : _right;
                        if (branch[node] == 0) branch[node] = nodes++;
                        node = branch[node];
                    }
                    _symbols[node] = symbol;
                }
            }
            internal bool Read(ref ulong bits, ref int count, out int symbol)
            {
                var node = 0;
                for (var used = 0; used < count; used++)
                {
                    node = ((bits >> used) & 1) == 0 ? _left[node] : _right[node];
                    if (node == 0) throw new InvalidDataException("Invalid DEFLATE Huffman code.");
                    if (_symbols[node] < 0) continue;
                    var consumed = used + 1;
                    bits >>= consumed;
                    count -= consumed;
                    symbol = _symbols[node];
                    return true;
                }
                symbol = 0;
                return false;
            }
        }
    }
}
