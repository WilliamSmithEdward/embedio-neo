using System;

namespace EmbedIO
{
    /// <summary>Selects the directions of a <see cref="WebTransportStream"/> that an abort ends.</summary>
    [Flags]
    public enum WebTransportAbortDirection
    {
        /// <summary>Stops receiving: the peer is asked to stop sending on the stream.</summary>
        Read = 1,

        /// <summary>Stops sending: the stream is reset, and data not yet delivered is discarded.</summary>
        Write = 2,

        /// <summary>Ends both directions.</summary>
        Both = Read | Write
    }
}
