using System;
using System.Collections.Generic;
using System.IO;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    internal static partial class QpackEncoder
    {
        internal sealed class FieldSection
        {
            internal FieldSection(byte[] wire, long[] references) { Wire = wire; References = references; }
            public byte[] Wire { get; }
            public long[] References { get; }
        }

        // The encoder owner supplies exact table matches (-1 means no match).
        // It must pin the returned references before publishing this section.
        // Base equals Required Insert Count, so all references are pre-base.
        internal static FieldSection EncodeReferenced(HpackField[] fields, long[] indices, long maximumTableCapacity,
            int maximumEncodedBytes, int maximumDecodedBytes)
        {
            Validate(fields, maximumEncodedBytes, maximumDecodedBytes);
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (indices.Length != fields.Length) throw new ArgumentException("QPACK index count differs from field count.", nameof(indices));
            if (maximumTableCapacity < 0 || maximumTableCapacity > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(maximumTableCapacity));
            var references = new HashSet<long>();
            long required = 0;
            for (var i = 0; i < fields.Length; ++i)
            {
                var index = indices[i];
                if (index < -1 || index >= QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(indices));
                if (!UseDynamic(fields[i], index)) continue;
                ValidateOctets(fields[i].Name);
                ValidateOctets(fields[i].Value);
                references.Add(index);
                required = Math.Max(required, index + 1);
            }
            var entries = maximumTableCapacity / 32;
            if (required != 0 && entries == 0) throw new ArgumentException("Dynamic QPACK references require table capacity.", nameof(maximumTableCapacity));
            var encodedCount = required == 0 ? 0 : required % (2 * entries) + 1;
            var output = RentOutput();
            byte[] wire;
            try
            {
                Ensure(output, IntegerLength(encodedCount, 8) + 1, maximumEncodedBytes);
                QpackInteger.Write(output, encodedCount, 8, 0);
                output.WriteByte(0);
                for (var i = 0; i < fields.Length; ++i)
                {
                    if (UseDynamic(fields[i], indices[i]))
                    {
                        var relative = required - indices[i] - 1;
                        Ensure(output, IntegerLength(relative, 6), maximumEncodedBytes);
                        QpackInteger.Write(output, relative, 6, 128);
                    }
                    else WriteField(output, fields[i], maximumEncodedBytes);
                }
                wire = output.ToArray();
            }
            finally { ReturnOutput(output); }
            var owned = new long[references.Count];
            references.CopyTo(owned);
            return new FieldSection(wire, owned);
        }
        private static bool UseDynamic(HpackField field, long index) => index >= 0 && !field.NeverIndexed
            && !Sensitive(field.Name) && !Exact.ContainsKey((field.Name, field.Value));
        private static void ValidateOctets(string value)
        {
            foreach (var octet in value)
                if (octet > 255) throw new ArgumentException("QPACK fields must contain octets.", nameof(value));
        }
    }
}
