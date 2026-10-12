using System;
using EmbedIO.Net.Internal;

namespace EmbedIO.Internal
{
    internal sealed class RequestBodyPolicy
    {
        internal RequestBodyPolicy(long maximumBytes, int? maximumHeaderBytes = null, int? maximumHeaderCount = null)
        {
            if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            MaximumBytes = maximumBytes;
            MaximumHeaderBytes = maximumHeaderBytes;
            MaximumHeaderCount = maximumHeaderCount;
        }

        internal long MaximumBytes { get; }
        private int? MaximumHeaderBytes { get; }
        private int? MaximumHeaderCount { get; }

        internal static RawRequestBodyLimitException? KnownRefusal(IHttpRequest request)
            => request switch
            {
                HttpListenerRequest http1 => http1.KnownBodyRefusal,
                MultiplexedRequest multiplexed => multiplexed.KnownBodyRefusal,
                _ => null,
            };

        // CONNECT carries a negotiated transport, not an ordinary HTTP entity.
        // HTTP/1 Upgrade requests still have their separately framed entity body.
        internal void Apply(IHttpRequest request)
        {
            if (request.HttpMethod == "CONNECT") return;
            if (request is HttpListenerRequest http1) http1.SetBodyPolicy(this);
            else if (request is MultiplexedRequest multiplexed) multiplexed.SetBodyPolicy(this);
            else throw new NotSupportedException("Raw request body limits require an owned request backend.");
            // Preserve the routed owner's header policy precedence. Its pipeline
            // formats 431 before admission; the raw view remains available to a
            // custom error handler without adding a second body counter.
            if (request is IRequestPolicyTarget fields
                && (fields.HeaderFieldBytes > MaximumHeaderBytes || fields.HeaderFieldCount > MaximumHeaderCount))
                return;
            if (request.ContentLength64 <= MaximumBytes) return;
            // Keep one request-owned failure until the selected owner's existing
            // pipeline handles it. Actors must not render or bypass callbacks.
            var failure = new RawRequestBodyLimitException();
            if (request is HttpListenerRequest ownedHttp1) ownedHttp1.MarkKnownBodyRefusal(failure);
            else if (request is MultiplexedRequest ownedMultiplexed) ownedMultiplexed.MarkKnownBodyRefusal(failure);
        }
    }

}
