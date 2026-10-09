using System.Runtime.CompilerServices;
using System.Threading;
using System.IO;

namespace EmbedIO.Internal
{
    internal sealed class RequestDecompressionPolicy
    {
        private static ConditionalWeakTable<IHttpContext, RequestDecompressionPolicy>? _policies;
        internal RequestDecompressionPolicy(long maximumBytes) { MaximumBytes = maximumBytes; }
        internal long MaximumBytes { get; }
        internal static void Associate(IHttpContext context, RequestDecompressionPolicy? policy)
        {
            var policies = Volatile.Read(ref _policies);
            if (policy == null) { policies?.Remove(context); return; }
            policies = LazyInitializer.EnsureInitialized(ref _policies)
                ?? throw new System.InvalidOperationException("Missing request policy registry.");
            policies.Remove(context);
            policies.Add(context, policy);
        }
        internal static Stream Apply(IHttpContext context, Stream decoded)
        {
            var policies = Volatile.Read(ref _policies);
            return policies != null && policies.TryGetValue(context, out var policy)
                ? new LimitedRequestStream(decoded, policy.MaximumBytes) : decoded;
        }
    }
}
