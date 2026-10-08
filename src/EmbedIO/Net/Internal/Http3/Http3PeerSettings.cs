using System;
using System.Collections.Generic;
using System.IO;

namespace EmbedIO.Net.Internal.Http3
{
    // Immutable peer limits. Parsing does not allocate a QPACK table or enable extensions.
    internal sealed class Http3PeerSettings
    {
        public long MaximumTableCapacity { get; private set; }
        public long BlockedStreams { get; private set; }
        public long MaximumFieldSectionSize { get; private set; } = long.MaxValue;
        public bool ExtendedConnect { get; private set; }
        public bool Datagrams { get; private set; }

        internal static Http3PeerSettings Parse(byte[] payload, int maximumEntries = 1024)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (maximumEntries < 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            var settings = new Http3PeerSettings();
            var seen = new HashSet<long>();
            var offset = 0;
            try
            {
                while (offset < payload.Length)
                {
                    if (seen.Count == maximumEntries) throw new Http3ProtocolException(0x107, "Too many HTTP/3 settings.");
                    var id = QuicInteger.Read(payload, ref offset, payload.Length);
                    var value = QuicInteger.Read(payload, ref offset, payload.Length);
                    if (!seen.Add(id)) throw new Http3ProtocolException(0x109, "Duplicate HTTP/3 setting identifier.");
                    switch (id)
                    {
                        case 1: settings.MaximumTableCapacity = value; break;
                        case 2:
                        case 3:
                        case 4:
                        case 5:
                            throw new Http3ProtocolException(0x109, "HTTP/2-only setting received in HTTP/3.");
                        case 6: settings.MaximumFieldSectionSize = value; break;
                        case 7: settings.BlockedStreams = value; break;
                        case 8: settings.ExtendedConnect = Boolean(value); break;
                        case 0x33: settings.Datagrams = Boolean(value); break;
                    }
                }
            }
            catch (EndOfStreamException) { throw new Http3ProtocolException(0x106, "Truncated HTTP/3 setting."); }
            return settings;
        }

        private static bool Boolean(long value)
        {
            if (value > 1) throw new Http3ProtocolException(0x109, "Invalid HTTP/3 boolean setting.");
            return value != 0;
        }
    }
}
