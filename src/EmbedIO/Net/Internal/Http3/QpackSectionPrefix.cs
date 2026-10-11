using System;
using System.IO;

namespace EmbedIO.Net.Internal.Http3
{
    // RFC 9204 section 4.5.1. Field-line decoding must separately validate every
    // referenced entry and enforce the advertised blocked-stream limit.
    internal readonly struct QpackSectionPrefix
    {
        private QpackSectionPrefix(long requiredInsertCount, long baseIndex)
        { RequiredInsertCount = requiredInsertCount; Base = baseIndex; }
        public long RequiredInsertCount { get; }
        public long Base { get; }

        internal static QpackSectionPrefix Read(byte[] bytes, ref int offset, int end, long maximumCapacity, long totalInserts)
        {
            ValidateCounter(maximumCapacity, nameof(maximumCapacity));
            ValidateCounter(totalInserts, nameof(totalInserts));
            var cursor = offset;
            try
            {
                var encoded = QpackInteger.Read(bytes, ref cursor, end, 8);
                var entries = maximumCapacity / 32;
                var required = 0L;
                if (encoded != 0)
                {
                    var range = 2 * entries;
                    if (encoded > range) throw Invalid("Required Insert Count exceeds the table's encoding range.");
                    var upper = totalInserts + entries;
                    required = upper / range * range + encoded - 1;
                    if (required > upper)
                    {
                        if (required <= range) throw Invalid("Invalid wrapped Required Insert Count.");
                        required -= range;
                    }
                    if (required == 0 || required > QuicInteger.Maximum) throw Invalid("Invalid Required Insert Count.");
                }
                if (cursor == end) throw Invalid("Missing QPACK Delta Base.");
                var negative = (bytes[cursor] & 128) != 0;
                var delta = QpackInteger.Read(bytes, ref cursor, end, 7);
                long baseIndex;
                if (negative)
                {
                    if (delta >= required) throw Invalid("QPACK Base cannot be negative.");
                    baseIndex = required - delta - 1;
                }
                else
                {
                    if (delta > QuicInteger.Maximum - required) throw Invalid("QPACK Base exceeds the supported counter range.");
                    baseIndex = required + delta;
                }
                offset = cursor;
                return new QpackSectionPrefix(required, baseIndex);
            }
            catch (EndOfStreamException) { throw Invalid("Truncated QPACK field section prefix."); }
            catch (InvalidDataException) { throw Invalid("Invalid QPACK prefix integer."); }
        }

        internal static byte[] Encode(long requiredInsertCount, long baseIndex, long maximumCapacity)
        {
            ValidateCounter(requiredInsertCount, nameof(requiredInsertCount));
            ValidateCounter(baseIndex, nameof(baseIndex));
            ValidateCounter(maximumCapacity, nameof(maximumCapacity));
            var range = 2 * (maximumCapacity / 32);
            if (requiredInsertCount != 0 && range == 0) throw new ArgumentException("Dynamic references require table capacity.", nameof(maximumCapacity));
            using var output = new MemoryStream(20);
            QpackInteger.Write(output, requiredInsertCount == 0 ? 0 : requiredInsertCount % range + 1, 8, 0);
            var negative = baseIndex < requiredInsertCount;
            QpackInteger.Write(output, negative ? requiredInsertCount - baseIndex - 1 : baseIndex - requiredInsertCount, 7, (byte)(negative ? 128 : 0));
            return output.ToArray();
        }

        private static void ValidateCounter(long value, string name)
        { if (value < 0 || value > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(name); }
        private static Http3ProtocolException Invalid(string reason) => new(0x200, reason);
    }
}
