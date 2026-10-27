using System;
using System.Buffers.Binary;
using System.IO;

namespace EmbedIO.Quic
{
    // RFC 9000 section 16. All cursor operations reject truncated peer input, and a length above
    // the caller's limit, with an InvalidDataException, or with a TransportException carrying the
    // error code the reader was given: frames are read with FRAME_ENCODING_ERROR (section 20.1).
    internal ref struct WireReader(ReadOnlySpan<byte> bytes, ulong? errorCode = null)
    {
        private ReadOnlySpan<byte> _remaining = bytes;
        private readonly ulong? _errorCode = errorCode;

        internal readonly int Remaining => _remaining.Length;

        internal byte Byte() => Take(1)[0];

        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length) throw Malformed("Truncated QUIC input.");

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

            if (value > (ulong)maximum) throw Malformed("QUIC length exceeds the configured limit.");

            return (int)value;
        }

        private readonly Exception Malformed(string message) =>
            _errorCode is { } code ? new TransportException(code, message) : new InvalidDataException(message);
    }

    internal static class Wire
    {
        internal static void Integer(Stream target, ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            target.Write(bytes[..Integer(bytes, value)]);
        }

        internal static int IntegerLength(ulong value)
        {
            if (value >= 1UL << 62) throw new ArgumentOutOfRangeException(nameof(value));

            return value < 64 ? 1 : value < 16384 ? 2 : value < 1073741824 ? 4 : 8;
        }

        // Writes the variable-length integer at the start of target and returns its length.
        internal static int Integer(Span<byte> target, ulong value)
        {
            var length = IntegerLength(value);

            switch (length)
            {
                case 1:
                    target[0] = (byte)value;
                    break;

                case 2:
                    BinaryPrimitives.WriteUInt16BigEndian(target, (ushort)(value | 0x4000));
                    break;

                case 4:
                    BinaryPrimitives.WriteUInt32BigEndian(target, (uint)value | 0x80000000);
                    break;

                default:
                    BinaryPrimitives.WriteUInt64BigEndian(target, value | 0xc000000000000000);
                    break;
            }

            return length;
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

        internal static byte[] Frame(params ReadOnlySpan<ulong> values)
        {
            var length = 0;

            foreach (var value in values) length += IntegerLength(value);

            var frame = new byte[length];
            var offset = 0;

            foreach (var value in values) offset += Integer(frame.AsSpan(offset), value);

            return frame;
        }
    }

    // Fixed bounds limit CRYPTO reassembly independently of packet lifetime.
    // Retaining consumed bytes lets overlapping retransmissions be checked exactly.
    // Storage grows with the highest offset received, up to the bound: a handshake
    // flight is a few kilobytes of a 64 KiB bound. Data past the bound, including any
    // past the largest offset a stream can carry, is CRYPTO_BUFFER_EXCEEDED (RFC 9000
    // sections 7.5 and 19.6).
    internal sealed class Reassembly(int capacity)
    {
        private readonly int _capacity = capacity >= 0
            ? capacity
            : throw new ArgumentOutOfRangeException(nameof(capacity));
        private byte[] _bytes = [];
        private bool[] _present = [];
        private int _contiguous;
        private int _consumed;
        private ulong? _finalSize;

        internal int Contiguous => _contiguous;
        internal bool Complete => _finalSize == (ulong)_contiguous;

        internal void Add(ulong offset, ReadOnlySpan<byte> bytes, bool final = false)
        {
            if (offset > (ulong)_capacity || (ulong)bytes.Length > (ulong)_capacity - offset)
                throw new TransportException(0x0d, "Reassembly budget exceeded.");

            var end = offset + (ulong)bytes.Length;

            if (_finalSize.HasValue && (end > _finalSize.Value || (final && end != _finalSize.Value)))
                throw new InvalidDataException("Conflicting final size.");

            if (final)
            {
                for (var i = (int)end; i < _present.Length; i++)
                    if (_present[i]) throw new InvalidDataException("Final size precedes received bytes.");

                _finalSize = end;
            }

            if ((int)end > _bytes.Length)
            {
                var size = (int)Math.Min(_capacity, Math.Max((long)end, Math.Max(1024L, 2L * _bytes.Length)));
                Array.Resize(ref _bytes, size);
                Array.Resize(ref _present, size);
            }

            for (var i = 0; i < bytes.Length; i++)
            {
                var index = (int)offset + i;

                if (_present[index] && _bytes[index] != bytes[i])
                    throw new InvalidDataException("Conflicting overlapping data.");

                _bytes[index] = bytes[i];
                _present[index] = true;
            }

            while (_contiguous < _present.Length && _present[_contiguous]) _contiguous++;
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
