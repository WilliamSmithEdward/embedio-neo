using System;
using System.Reflection;
using System.Threading.Tasks;

namespace EmbedIO.Tests.TestObjects
{
    // The tests run on .NET 10, but can select the actual netstandard2.0 core.
    // Keep modern DisposeAsync coverage while using that asset's public CloseAsync.
    internal static class TunnelDisposal
    {
        private static readonly Func<HttpTunnel, ValueTask>? ModernDisposal = FindModernDisposal();
        internal static Task BeginAsync(HttpTunnel tunnel)
            => ModernDisposal == null ? tunnel.CloseAsync() : ModernDisposal(tunnel).AsTask();
        private static Func<HttpTunnel, ValueTask>? FindModernDisposal()
        {
            var method = typeof(HttpTunnel).GetMethod("DisposeAsync", BindingFlags.Instance | BindingFlags.Public,
                null, Type.EmptyTypes, null);
            return method?.CreateDelegate<Func<HttpTunnel, ValueTask>>();
        }
    }
}
