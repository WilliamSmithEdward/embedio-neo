using System;

namespace EmbedIO.Internal
{
    // Implemented by engine request types so the server can bound the transferred body.
    // The limit wraps InputStream; transport framing checks keep using the raw stream.
    internal interface IRequestBodyLimit
    {
        // Must be applied before InputStream is first read. onExceeded runs once, on the
        // reading thread, when a read crosses the limit.
        void LimitBody(long maximumBytes, Action onExceeded);
    }
}
