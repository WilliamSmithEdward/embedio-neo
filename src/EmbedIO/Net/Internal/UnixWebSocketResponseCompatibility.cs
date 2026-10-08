using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EmbedIO.Net.Internal
{
    // Unix HttpListener leaves SentHeaders false after its successful upgrade.
    // Closing the response can then serialize HTTP headers into the WebSocket
    // transport, racing WebSocket cancellation/disposal of that same stream.
    internal static class UnixWebSocketResponseCompatibility
    {
        private static readonly PropertyInfo? SentHeaders = typeof(System.Net.HttpListenerResponse)
            .GetProperty("SentHeaders", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void CompleteUpgrade(System.Net.HttpListenerResponse response)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            // Preserve native behavior on unrecognized runtimes. This mitigation
            // is validated against .NET 10; the managed backend avoids the shim.
            if (SentHeaders?.PropertyType == typeof(bool) && SentHeaders.SetMethod != null)
                SentHeaders.SetValue(response, true);
        }
    }
}
