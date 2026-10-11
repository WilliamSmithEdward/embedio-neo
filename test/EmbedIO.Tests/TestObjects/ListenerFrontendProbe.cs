using System.Collections;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace EmbedIO.Tests.TestObjects
{
    internal static class ListenerFrontendProbe
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static object Field(object owner, string name)
            => owner.GetType().GetField(name, Hidden)?.GetValue(owner) ?? throw new AssertionException("Missing frontend owner field " + name);
        internal static object Coordinator(object listener) => Field(listener, "_coordinator");
        internal static object Epoch(object listener) => Field(Coordinator(listener), "_epoch");
        internal static IDictionary Pending(object listener) => (IDictionary)Field(Epoch(listener), "_pending");
        internal static ICollection Order(object listener) => (ICollection)Field(Epoch(listener), "_order");
        internal static SemaphoreSlim Wake(object listener) => (SemaphoreSlim)Field(Epoch(listener), "_wake");
        internal static IDictionary Sessions(object listener) => (IDictionary)Field(Coordinator(listener), "_sessions");
    }
}
