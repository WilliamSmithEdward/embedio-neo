using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;

namespace EmbedIO
{
    /// <summary>Reads and writes streamed capsules on an already negotiated reliable carrier.</summary>
    /// <remarks>
    /// The caller owns the stream and its closure, negotiation, deadlines and resource policy.
    /// One operation per direction may run at a time; reading and writing may run concurrently.
    /// This type does not negotiate HTTP tunnels or provide unreliable QUIC datagram delivery.
    /// </remarks>
    public sealed class HttpCapsuleChannel
    {
        private readonly HttpCapsuleTransport _transport;
        /// <summary>Creates a capsule channel without taking ownership of its stream.</summary>
        /// <param name="stream">The already negotiated carrier.</param>
        public HttpCapsuleChannel(Stream stream) : this(stream, null) { }
        internal HttpCapsuleChannel(Stream stream, Func<Exception, Task>? abort)
        { _transport = new HttpCapsuleTransport(stream, abort); }
        /// <summary>Reads the next header after the previous payload is consumed or skipped.</summary>
        /// <param name="cancellationToken">Cancellation for this operation.</param>
        /// <returns>The next header, or null at a complete carrier end.</returns>
        public Task<HttpCapsuleHeader?> ReadHeaderAsync(CancellationToken cancellationToken = default)
            => _transport.ReadHeaderAsync(cancellationToken);
        /// <summary>Reads part of the current payload without crossing its boundary.</summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <param name="offset">Destination offset.</param>
        /// <param name="count">Maximum bytes to read.</param>
        /// <param name="cancellationToken">Cancellation for this operation.</param>
        /// <returns>Bytes read; zero means completed payload or zero count. Truncation throws.</returns>
        public Task<int> ReadPayloadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
            => _transport.ReadPayloadAsync(buffer, offset, count, cancellationToken);
        /// <summary>Discards the current payload incrementally using bounded temporary storage.</summary>
        /// <param name="cancellationToken">Cancellation for this operation.</param>
        /// <returns>A task completing after the payload is skipped.</returns>
        public Task SkipPayloadAsync(CancellationToken cancellationToken = default)
            => _transport.SkipPayloadAsync(cancellationToken);
        /// <summary>Writes a header after the previous payload is written completely.</summary>
        /// <param name="type">Unsigned 62-bit capsule type.</param>
        /// <param name="length">Unsigned 62-bit payload length.</param>
        /// <param name="cancellationToken">Cancellation for this operation.</param>
        /// <returns>A task completing after the header write.</returns>
        public Task WriteHeaderAsync(long type, long length, CancellationToken cancellationToken = default)
            => _transport.WriteHeaderAsync(type, length, cancellationToken);
        /// <summary>Writes part of the current payload without exceeding its declared length.</summary>
        /// <param name="buffer">Buffer borrowed until the returned task completes.</param>
        /// <param name="offset">Payload offset.</param>
        /// <param name="count">Bytes to write.</param>
        /// <param name="cancellationToken">Cancellation for this operation.</param>
        /// <returns>A task completing after the payload write.</returns>
        public Task WritePayloadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
            => _transport.WritePayloadAsync(buffer, offset, count, cancellationToken);
        /// <summary>Validates completion and prevents later writes without closing the borrowed stream.</summary>
        /// <remarks>Await pending writes first. An unfinished payload is an error. This does not send transport FIN.</remarks>
        public void CompleteOutput() => _transport.CompleteOutput();
    }
}
