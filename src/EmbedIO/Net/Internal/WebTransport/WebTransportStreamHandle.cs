using System;
using System.Threading;

namespace EmbedIO.Net.Internal.WebTransport
{
    // The session core's view of one data stream. The integrating transport
    // wraps its QUIC stream in a handle; the core only needs the identity, the
    // direction and a way to tear the stream down with an error code once.
    internal sealed class WebTransportStreamHandle
    {
        private readonly Action<long> _abort;
        private long _abortErrorCode = -1;

        // abort must reset the send side and stop reading with the given HTTP/3
        // error code. Draft section 4.4 requires RESET_STREAM_AT to keep at least
        // the stream header reliable; a provider without that frame must deliver
        // the header before resetting, which this core cannot verify.
        internal WebTransportStreamHandle(long id, bool bidirectional, Action<long> abort)
        {
            if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
            Id = id;
            Bidirectional = bidirectional;
            _abort = abort ?? throw new ArgumentNullException(nameof(abort));
        }

        public long Id { get; }
        public bool Bidirectional { get; }
        // The code of the first abort, or -1 while the stream is intact.
        public long AbortErrorCode => Volatile.Read(ref _abortErrorCode);

        internal void Abort(long errorCode)
        {
            if (errorCode < 0) throw new ArgumentOutOfRangeException(nameof(errorCode));
            if (Interlocked.CompareExchange(ref _abortErrorCode, errorCode, -1) != -1) return;
            _abort(errorCode);
        }
    }
}
