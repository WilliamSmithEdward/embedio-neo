#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

namespace EmbedIO.Net.Internal.Http3
{
    internal static class MsQuicAddress
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint GetParameter(IntPtr owner, uint parameter, ref uint size, IntPtr buffer);

        // Callers retain the handle for the whole query. Parameter identifiers
        // follow the pinned v2.6.2 public ABI, not runtime-private fields:
        // https://github.com/microsoft/msquic/blob/v2.6.2/src/inc/msquic.h
        internal static IPEndPoint Read(GetParameter query, IntPtr owner, uint parameter)
        {
            var buffer = Marshal.AllocHGlobal(28);

            try
            {
                uint size = 28;
                var status = query(owner, parameter, ref size, buffer);

                if (MsQuicApi.Failed(status) || size > 28 || size < 16)
                    throw new IOException("MsQuic address query failed.");

                var bytes = new byte[28];
                Marshal.Copy(buffer, bytes, 0, (int)size);
                var family = OperatingSystem.IsMacOS() ? bytes[1] : BitConverter.ToUInt16(bytes, 0);
                var port = (bytes[2] << 8) | bytes[3];

                if (family == 2) return new IPEndPoint(new IPAddress(bytes.AsSpan(4, 4)), port);

                var ipv6 = OperatingSystem.IsWindows() ? 23 : OperatingSystem.IsMacOS() ? 30 : 10;

                if (family != ipv6 || size < 28) throw new IOException("MsQuic returned an invalid address family.");

                return new IPEndPoint(new IPAddress(bytes.AsSpan(8, 16), BitConverter.ToUInt32(bytes, 24)), port);
            }

            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
#endif
