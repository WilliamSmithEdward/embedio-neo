using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EmbedIO.Internal
{
    // The decoded-byte limit of compressed request bodies. The owned listeners capture it
    // with the request's policy snapshot at admission, so a request pays nothing for it
    // until a body is decoded. Other context implementations register it here per request.
    internal sealed class RequestDecompressionPolicy
    {
        private static ConditionalWeakTable<IHttpContext, RequestDecompressionPolicy>? _policies;

        private RequestDecompressionPolicy(long maximumBytes) => MaximumBytes = maximumBytes;

        internal long MaximumBytes { get; }

        internal static void Associate(IHttpContext context, long? maximumBytes)
        {
            var policies = Volatile.Read(ref _policies);

            if (maximumBytes is not long maximum)
            {
                policies?.Remove(context);
                return;
            }

            policies = LazyInitializer.EnsureInitialized(ref _policies)
                ?? throw new System.InvalidOperationException("Missing request policy registry.");
            policies.Remove(context);
            policies.Add(context, new RequestDecompressionPolicy(maximum));
        }

        internal static Stream Apply(IHttpContext context, Stream decoded)
        {
            long? limit;

            if (RequestPolicySnapshot.CapturedBy(context.Request) is { } captured) limit = captured.MaximumDecodedBytes;

            else
            {
                var policies = Volatile.Read(ref _policies);
                limit = policies != null && policies.TryGetValue(context, out var policy) ? policy.MaximumBytes : null;
            }

            return limit is long maximum ? new LimitedRequestStream(decoded, maximum) : decoded;
        }
    }
}
