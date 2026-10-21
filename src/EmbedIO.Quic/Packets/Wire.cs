using System;
using System.Buffers.Binary;
using System.IO;

namespace EmbedIO.Quic
{
    // RFC 9000 section 16. All cursor operations reject truncated peer input.
    internal ref struct WireReader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;

        internal readonly int Remaining => _remaining.Length;

        internal byte Byte() => Take(1)[0];

        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length) throw new InvalidDataException("Truncated QUIC input.");

            var result = _remaining[..count];
            _remaining = _remaining[count..];
            return result;
        }

        internal ulong Integer()
        {
            var first = Byte();
            var length = 1 << (first >> 6);
            var value = (ulong)(first & 63);

            for (var i = 1; i < length; i++) value = (value << 8) | Byte();

            return value;
        }

        internal int Length(int maximum)
        {
            var value = Integer();

            if (value > (ulong)maximum) throw new InvalidDataException("QUIC length exceeds the experiment limit.");

            return (int)value;
        }
    }

    internal static class Wire
    {
        internal static void Integer(Stream target, ulong value)
        {
            if (value >= 1UL << 62) throw new ArgumentOutOfRangeException(nameof(value));

            var length = value < 64 ? 1 : value < 16384 ? 2 : value < 1073741824 ? 4 : 8;
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            bytes[8 - length] |=
                (byte)(length switch
                {
                    1 => 0,
                    2 => 64,
                    4 => 128,
                    _ => 192
                });
            target.Write(bytes[(8 - length)..]);
        }

        internal static void Parameter(Stream target, ulong id, ReadOnlySpan<byte> value)
        {
            Integer(target, id);
            Integer(target, (ulong)value.Length);
            target.Write(value);
        }

        internal static void Parameter(Stream target, ulong id, ulong value)
        {
            using var encoded = new MemoryStream();
            Integer(encoded, value);
            Parameter(target, id, encoded.ToArray());
        }

        internal static byte[] Frame(params ulong[] values)
        {
            using var encoded = new MemoryStream();

            foreach (var value in values) Integer(encoded, value);

            return encoded.ToArray();
        }
    }

    // Fixed bounds are deliberate for this loopback-only feasibility probe.
    // Retaining consumed bytes lets overlapping retransmissions be checked exactly.
    internal sealed class Reassembly(int capacity)
    {
        private readonly byte[] _bytes = new byte[capacity];
        private readonly bool[] _present = new bool[capacity];
        private int _contiguous;
        private int _consumed;
        private ulong? _finalSize;

        internal int Contiguous => _contiguous;
        internal bool Complete => _finalSize == (ulong)_contiguous;

        internal void Add(ulong offset, ReadOnlySpan<byte> bytes, bool final = false)
        {
            if (offset > (ulong)_bytes.Length || (ulong)bytes.Length > (ulong)_bytes.Length - offset)
                throw new InvalidDataException("Reassembly budget exceeded.");

            var end = offset + (ulong)bytes.Length;

            if (_finalSize.HasValue && (end > _finalSize.Value || (final && end != _finalSize.Value)))
                throw new InvalidDataException("Conflicting final size.");

            if (final)
            {
                for (var i = (int)end; i < _present.Length; i++)
                    if (_present[i]) throw new InvalidDataException("Final size precedes received bytes.");

                _finalSize = end;
            }

            for (var i = 0; i < bytes.Length; i++)
            {
                var index = (int)offset + i;

                if (_present[index] && _bytes[index] != bytes[i])
                    throw new InvalidDataException("Conflicting overlapping data.");

                _bytes[index] = bytes[i];
                _present[index] = true;
            }

            while (_contiguous < _bytes.Length && _present[_contiguous]) _contiguous++;
        }

        internal byte[] Consume(int maximum = int.MaxValue)
        {
            var result = _bytes
                .AsSpan(_consumed, Math.Max(0, Math.Min(_contiguous, maximum) - _consumed))
                .ToArray();
            _consumed += result.Length;
            return result;
        }
    }
}
