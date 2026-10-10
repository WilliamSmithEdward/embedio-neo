#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Net.Quic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbedIO.Net.Internal.Http3
{
    // Original managed binding to the public, stable MsQuic API v2. A child
    // handle holds a SafeHandle reference to its API table and loaded library.
    internal sealed class MsQuicApi : SafeHandleZeroOrMinusOneIsInvalid
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint OpenApi(uint version, out IntPtr table);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseApi(IntPtr table);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint OpenRegistration(IntPtr configuration, out IntPtr registration);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void CloseRegistration(IntPtr registration);

        private readonly IntPtr _library;
        private readonly CloseApi _close;
        private readonly OpenRegistration _registrationOpen;
        private readonly CloseRegistration _registrationClose;

        private MsQuicApi(IntPtr library, IntPtr table, CloseApi close) : base(true)
        {
            _library = library;
            _close = close;
            // Stable API v2 slots, confirmed against the published msquic.h ABI.
            _registrationOpen = Marshal.GetDelegateForFunctionPointer<OpenRegistration>(Marshal.ReadIntPtr(table, 5 * IntPtr.Size));
            _registrationClose = Marshal.GetDelegateForFunctionPointer<CloseRegistration>(Marshal.ReadIntPtr(table, 6 * IntPtr.Size));
            SetHandle(table);
        }

        internal static MsQuicApi Open()
        {
            var name = OperatingSystem.IsWindows() ? "msquic" : OperatingSystem.IsMacOS() ? "libmsquic.2.dylib" : "libmsquic.so.2";
            var library = NativeLibrary.Load(name, typeof(QuicConnection).Assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories);
            IntPtr table = IntPtr.Zero;
            CloseApi? close = null;
            try
            {
                var open = Marshal.GetDelegateForFunctionPointer<OpenApi>(NativeLibrary.GetExport(library, "MsQuicOpenVersion"));
                close = Marshal.GetDelegateForFunctionPointer<CloseApi>(NativeLibrary.GetExport(library, "MsQuicClose"));
                var status = open(2, out table);
                if (status != 0) throw new IOException("MsQuic API initialization failed with status 0x" + status.ToString("X8"));
                if (table == IntPtr.Zero) throw new IOException("MsQuic returned an empty API table.");
                return new MsQuicApi(library, table, close);
            }
            catch
            {
                if (table != IntPtr.Zero) close?.Invoke(table);
                NativeLibrary.Free(library);
                throw;
            }
        }

        internal MsQuicRegistration CreateRegistration()
        {
            var retained = false;
            DangerousAddRef(ref retained);
            IntPtr registration = IntPtr.Zero;
            try
            {
                var status = _registrationOpen(IntPtr.Zero, out registration);
                if (status != 0) throw new IOException("MsQuic registration failed with status 0x" + status.ToString("X8"));
                if (registration == IntPtr.Zero) throw new IOException("MsQuic returned an empty registration.");
                var result = new MsQuicRegistration(registration, this, _registrationClose);
                registration = IntPtr.Zero;
                retained = false; // The registration now owns this reference.
                return result;
            }
            finally
            {
                if (registration != IntPtr.Zero) _registrationClose(registration);
                if (retained) DangerousRelease();
            }
        }

        protected override bool ReleaseHandle()
        {
            try { _close(handle); }
            finally { NativeLibrary.Free(_library); }
            return true;
        }
    }

    internal sealed class MsQuicRegistration : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly MsQuicApi _api;
        private readonly MsQuicApi.CloseRegistration _close;
        internal MsQuicRegistration(IntPtr registration, MsQuicApi api, MsQuicApi.CloseRegistration close) : base(true)
        { _api = api; _close = close; SetHandle(registration); }
        protected override bool ReleaseHandle()
        {
            try { _close(handle); }
            finally { _api.DangerousRelease(); }
            return true;
        }
    }
}
#endif
