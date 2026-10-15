using System;
using System.Threading;

namespace EmbedIO
{
    /// <summary>Owns the storage of one received HTTP datagram.</summary>
    /// <remarks>Dispose after using Payload. Previously obtained memory must not be used after disposal.</remarks>
    public sealed class HttpDatagram : IDisposable
    {
        private readonly ReadOnlyMemory<byte> _payload;
        private IDisposable? _owner;
        internal HttpDatagram(ReadOnlyMemory<byte> payload, IDisposable owner)
        { _payload = payload; _owner = owner ?? throw new ArgumentNullException(nameof(owner)); }
        /// <summary>Gets the datagram payload, including a valid empty payload, until disposal.</summary>
        public ReadOnlyMemory<byte> Payload => Volatile.Read(ref _owner) != null
            ? _payload : throw new ObjectDisposedException(nameof(HttpDatagram));
        /// <summary>Releases the packet storage once.</summary>
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}
