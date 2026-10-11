using System;
using System.Linq;
using System.Net;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests.TestObjects
{
    internal static class TcpRegistrationInventory
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;

        internal static object[] Owned(Net.HttpListener listener)
        {
            var method = typeof(Net.EndPointManager).GetMethod("OwnedEndpoints", Hidden)
                ?? throw new AssertionException("Missing owned endpoint snapshot.");
            return (method.Invoke(null, new object[] { listener }) as Array
                ?? throw new AssertionException("Missing owned endpoint inventory.")).Cast<object>().ToArray();
        }

        internal static object Find(IPAddress address, int port)
            => (typeof(Net.EndPointManager).GetMethod("FindEndpoint", Hidden)
                ?? throw new AssertionException("Missing endpoint lookup."))
                .Invoke(null, new object[] { address, port }) ?? throw new AssertionException("Missing registered endpoint.");
    }
}
