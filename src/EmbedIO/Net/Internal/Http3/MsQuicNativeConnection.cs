#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace EmbedIO.Net.Internal.Http3
{
    internal sealed partial class MsQuicApi
    {
        private readonly ConnectionFunctions _connectionFunctions;
        internal sealed class ConnectionFunctions
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint Callback(IntPtr connection, IntPtr context, IntPtr eventData);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void SetCallback(IntPtr connection, IntPtr callback, IntPtr context);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void CloseConnection(IntPtr connection);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void ShutdownConnection(IntPtr connection, uint flags, ulong code);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint SetConfiguration(IntPtr connection, IntPtr configuration);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void CloseStream(IntPtr stream);
            internal readonly SetCallback SetHandler;
            internal readonly CloseConnection Close;
            internal readonly ShutdownConnection Shutdown;
            internal readonly SetConfiguration Configure;
            internal readonly CloseStream StreamClose;
            internal ConnectionFunctions(IntPtr table)
            {
                SetHandler = Marshal.GetDelegateForFunctionPointer<SetCallback>(Marshal.ReadIntPtr(table, 2 * IntPtr.Size));
                Close = Marshal.GetDelegateForFunctionPointer<CloseConnection>(Marshal.ReadIntPtr(table, 16 * IntPtr.Size));
                Shutdown = Marshal.GetDelegateForFunctionPointer<ShutdownConnection>(Marshal.ReadIntPtr(table, 17 * IntPtr.Size));
                Configure = Marshal.GetDelegateForFunctionPointer<SetConfiguration>(Marshal.ReadIntPtr(table, 19 * IntPtr.Size));
                StreamClose = Marshal.GetDelegateForFunctionPointer<CloseStream>(Marshal.ReadIntPtr(table, 22 * IntPtr.Size));
            }
        }
        internal MsQuicNativeConnection AcceptConnection(MsQuicRegistration registration, IntPtr connection)
            => MsQuicNativeConnection.Accept(registration, connection, _connectionFunctions);
    }
    internal sealed class MsQuicNativeConnection : SafeHandleZeroOrMinusOneIsInvalid
    {
        private sealed class Signals
        {
            internal readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly MsQuicApi.ConnectionFunctions.Callback Handler;
            internal readonly IntPtr Pointer;
            private readonly MsQuicApi.ConnectionFunctions _functions;
            internal Signals(MsQuicApi.ConnectionFunctions functions)
            { _functions = functions; Handler = OnEvent; Pointer = Marshal.GetFunctionPointerForDelegate(Handler); }
            private uint OnEvent(IntPtr connection, IntPtr context, IntPtr eventData)
            {
                try
                {
                    switch (Marshal.ReadInt32(eventData))
                    {
                        case 0: Connected.TrySetResult(); break;
                        case 1:
                        case 2: Connected.TrySetCanceled(); break;
                        case 3: Connected.TrySetCanceled(); Closed.TrySetResult(); break;
                        // Streams are not exposed until their own callback/lifetime exists.
                        case 6: _functions.StreamClose(Marshal.ReadIntPtr(eventData, 8)); break;
                    }
                    return 0;
                }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                { Connected.TrySetCanceled(); Closed.TrySetException(error); return OperatingSystem.IsWindows() ? 0x80004004u : OperatingSystem.IsMacOS() ? 89u : 125u; }
            }
        }
        private readonly MsQuicRegistration _registration;
        private readonly MsQuicApi.ConnectionFunctions _functions;
        private readonly Signals _signals;
        private int _shutdown;
        private MsQuicNativeConnection(MsQuicRegistration registration, MsQuicApi.ConnectionFunctions functions, Signals signals) : base(true)
        { _registration = registration; _functions = functions; _signals = signals; }
        internal static MsQuicNativeConnection Accept(MsQuicRegistration registration, IntPtr handle, MsQuicApi.ConnectionFunctions functions)
        {
            if (handle == IntPtr.Zero) throw new ArgumentException("Missing accepted connection.", nameof(handle));
            // Allocate all managed callback state before acquiring native ownership.
            var signals = new Signals(functions);
            var result = new MsQuicNativeConnection(registration, functions, signals);
            var retained = false;
            registration.DangerousAddRef(ref retained);
            try
            {
                functions.SetHandler(handle, signals.Pointer, IntPtr.Zero);
                result.SetHandle(handle);
                retained = false;
                return result;
            }
            finally { if (retained) registration.DangerousRelease(); }
        }
        internal Task Connected => _signals.Connected.Task;
        internal void Configure(MsQuicConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (!ReferenceEquals(configuration.Registration, _registration)) throw new ArgumentException("Configuration belongs to a different registration.", nameof(configuration));
            var retained = false;
            DangerousAddRef(ref retained);
            var configured = false;
            try
            {
                configuration.DangerousAddRef(ref configured);
                var status = _functions.Configure(handle, configuration.DangerousGetHandle());
                if (MsQuicApi.Failed(status)) throw new IOException("MsQuic connection configuration failed with status 0x" + status.ToString("X8"));
            }
            finally { if (configured) configuration.DangerousRelease(); if (retained) DangerousRelease(); }
        }
        internal Task ShutdownAsync(long code)
        {
            if (code < 0 || code > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(code));
            var retained = false; DangerousAddRef(ref retained);
            try { if (Interlocked.Exchange(ref _shutdown, 1) == 0) _functions.Shutdown(handle, 0, (ulong)code); return _signals.Closed.Task; }
            finally { if (retained) DangerousRelease(); }
        }
        protected override bool ReleaseHandle()
        {
            try { _functions.Close(handle); _signals.Connected.TrySetCanceled(); _signals.Closed.TrySetResult(); GC.KeepAlive(_signals); }
            finally { _registration.DangerousRelease(); }
            return true;
        }
    }
}
#endif
