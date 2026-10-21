using System;
using System.Collections.Generic;

namespace EmbedIO.Quic
{
    // The connection owns packet-key generations and the TLS session together.
    // Native providers publish managed state; packet processing never queries a
    // native handle merely to determine whether the handshake has completed.
    internal abstract class TlsSession : IDisposable
    {
        internal Queue<(int Level, byte[] Bytes)> Output { get; } = new();
        internal PacketKeys?[] ReadKeys { get; } = new PacketKeys?[3];
        internal PacketKeys?[] WriteKeys { get; } = new PacketKeys?[3];
        internal byte[]? PeerParameters { get; set; }
        internal byte? AlertCode { get; set; }
        internal bool Established { get; set; }

        internal abstract void Add(int level, ulong offset, ReadOnlySpan<byte> bytes);

        internal abstract void Pump();

        public abstract void Dispose();

        protected void ReleaseKeys()
        {
            foreach (var key in ReadKeys) key?.Dispose();

            foreach (var key in WriteKeys) key?.Dispose();

            Array.Clear(ReadKeys);
            Array.Clear(WriteKeys);
            Output.Clear();
            PeerParameters = null;
            Established = false;
        }
    }
}
