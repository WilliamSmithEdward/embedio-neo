namespace EmbedIO.Quic
{
    // Invoked only by the connection owner. Application consumption returns
    // receive credit; retirement additionally waits for the transport's ACKs.
    internal abstract class StreamApplication : IDisposable
    {
        internal virtual void Open(ulong id) { }

        internal virtual void Poll() { }

        internal virtual void WritesClosed(ulong id) { }

        internal virtual void ConnectionClosed(ulong error, bool application) { }

        internal abstract void Stream(ulong id, byte[] bytes, bool fin);

        internal abstract void Reset(ulong id, ulong error, ulong reliableSize);

        internal virtual void Stop(ulong id, ulong error) { }

        internal abstract void ReceiveDatagram(byte[] bytes);

        internal abstract bool CanRetire(ulong id);

        internal abstract void Retire(ulong id);

        public abstract void Dispose();
    }

    // Callbacks run only on the connection owner. Write transfers ownership of
    // its byte array until transmission or disposal; Consume returns only bytes
    // previously delivered to the application. The application borrows this port.
    internal sealed class StreamOperations(
        Action<ulong, byte[], bool> write,
        Action<ulong, byte[]> reliablePrefix,
        Action<byte[]> datagram,
        Action<ulong, long> abort,
        Action<ulong, int> consume,
        Func<ulong, ReadOnlyMemory<byte>, bool, bool> tryWrite,
        Func<bool, ulong?> open,
        Action<ulong, ulong> abortRead,
        Action<ulong, ulong> abortWrite,
        Func<byte[], bool> tryDatagram,
        int? datagramLimit,
        Action<ulong>? close,
        int inboundStreamCapacity,
        int streamCapacity
    )
    {
        internal Action<ulong, byte[], bool> Write { get; } = write;
        internal Action<ulong, byte[]> ReliablePrefix { get; } = reliablePrefix;
        internal Action<byte[]> Datagram { get; } = datagram;
        internal Action<ulong, long> Abort { get; } = abort;
        internal Action<ulong, int> Consume { get; } = consume;
        internal Func<ulong, ReadOnlyMemory<byte>, bool, bool> TryWrite { get; } = tryWrite;
        internal Func<bool, ulong?> Open { get; } = open;
        internal Action<ulong, ulong> AbortRead { get; } = abortRead;
        internal Action<ulong, ulong> AbortWrite { get; } = abortWrite;
        internal Func<byte[], bool> TryDatagram { get; } = tryDatagram;
        internal int? DatagramLimit { get; } = datagramLimit;
        internal Action<ulong>? Close { get; } = close;
        internal int InboundStreamCapacity { get; } = inboundStreamCapacity;
        internal int StreamCapacity { get; } = streamCapacity;
    }
}
