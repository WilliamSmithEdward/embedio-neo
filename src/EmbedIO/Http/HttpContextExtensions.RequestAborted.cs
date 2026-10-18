using System;
using System.Threading;

namespace EmbedIO
{
    public static partial class HttpContextExtensions
    {
        /// <summary>Gets an opt-in token canceled by an observed request abort or its current context token.</summary>
        /// <param name="this">The HTTP context.</param>
        /// <param name="cancelOnHttp1InputEnd">Whether HTTP/1 input closure also cancels the token.
        /// This includes valid half-closes; use only when the client will not half-close before reading.</param>
        /// <returns>A stable token owned by this request. Successful completion leaves it uncanceled.</returns>
        /// <exception cref="NotSupportedException">The context has no request-abort feature.</exception>
        /// <exception cref="InvalidOperationException">First acquisition is after completion or protocol handoff.</exception>
        public static CancellationToken GetRequestAbortedToken(
            this IHttpContext @this,
            bool cancelOnHttp1InputEnd = false
        )
        {
            if (@this == null) throw new ArgumentNullException(nameof(@this));

            return (@this as IHttpRequestAbortFeature
                ?? throw new NotSupportedException(
                    "This context does not support request-abort observation."
                )).GetRequestAbortedToken(cancelOnHttp1InputEnd);
        }

        /// <summary>Attempts to obtain a request-abort token from a supporting context.</summary>
        /// <param name="this">The HTTP context.</param>
        /// <param name="token">The request token, or the default token when unsupported.</param>
        /// <param name="cancelOnHttp1InputEnd">Whether HTTP/1 input closure also cancels the token.</param>
        /// <returns>Whether the context supports this feature. Lifecycle errors still throw.</returns>
        public static bool TryGetRequestAbortedToken(
            this IHttpContext @this,
            out CancellationToken token,
            bool cancelOnHttp1InputEnd = false
        )
        {
            if (@this == null) throw new ArgumentNullException(nameof(@this));

            if (@this is IHttpRequestAbortFeature feature)
            {
                token = feature.GetRequestAbortedToken(cancelOnHttp1InputEnd);
                return true;
            }

            token = default;
            return false;
        }
    }
}
