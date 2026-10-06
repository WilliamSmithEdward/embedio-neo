using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EmbedIO.Net.Internal
{
    // Unix HttpListener closes an unknown-length HEAD as chunked content and emits
    // a body terminator. No public API suppresses it while retaining the headers
    // and keep-alive policy. Validate the runtime shape before applying the shim.
    internal static class UnixHeadResponseCompatibility
    {
        private static readonly FieldInfo? TrailerSent = typeof(System.Net.HttpListenerResponse).Assembly
            .GetType("System.Net.HttpResponseStream", throwOnError: false)?
            .GetField("_trailer_sent", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void SuppressClosingChunk(Stream stream)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            if (TrailerSent == null || TrailerSent.FieldType != typeof(bool)
                || TrailerSent.DeclaringType != stream.GetType())
                throw new PlatformNotSupportedException("This native HttpListener runtime cannot safely complete HEAD responses. Use HttpListenerMode.EmbedIO.");

            // Body writes are already suppressed by SystemResponseStream. Mark only
            // the runtime's closing trailer as sent; normal headers and closure remain native.
            TrailerSent.SetValue(stream, true);
        }
    }
}
