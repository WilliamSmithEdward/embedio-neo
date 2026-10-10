#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace EmbedIO.Net.Internal.Http3
{
    internal sealed partial class MsQuicApi
    {
        private readonly ListenerFunctions _listenerFunctions;
        internal sealed class ListenerFunctions
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint Callback(IntPtr listener, IntPtr context, IntPtr eventData);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint OpenListener(IntPtr registration, Callback callback, IntPtr context, out IntPtr listener);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void CloseListener(IntPtr listener);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate uint StartListener(IntPtr listener, IntPtr alpn, uint count, IntPtr address);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void StopListener(IntPtr listener);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate uint GetParameter(IntPtr handle, uint parameter, ref uint size, IntPtr buffer);
            internal readonly OpenListener Open;
            internal readonly CloseListener Close;
            internal readonly StopListener Stop;
            private readonly StartListener _start;
            private readonly GetParameter _get;
            internal ListenerFunctions(IntPtr table)
            {
                Open = Marshal.GetDelegateForFunctionPointer<OpenListener>(Marshal.ReadIntPtr(table, 11 * IntPtr.Size));
                Close = Marshal.GetDelegateForFunctionPointer<CloseListener>(Marshal.ReadIntPtr(table, 12 * IntPtr.Size));
                _start = Marshal.GetDelegateForFunctionPointer<StartListener>(Marshal.ReadIntPtr(table, 13 * IntPtr.Size));
                Stop = Marshal.GetDelegateForFunctionPointer<StopListener>(Marshal.ReadIntPtr(table, 14 * IntPtr.Size));
                _get = Marshal.GetDelegateForFunctionPointer<GetParameter>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size));
            }
            internal void Start(IntPtr listener, IPEndPoint endpoint, byte[] alpn)
            {
                if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
                if (alpn == null) throw new ArgumentNullException(nameof(alpn));
                if (alpn.Length == 0 || alpn.Length > 255) throw new ArgumentOutOfRangeException(nameof(alpn));
                var ipv6 = endpoint.AddressFamily == AddressFamily.InterNetworkV6;
                if (!ipv6 && endpoint.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("An IP endpoint is required.", nameof(endpoint));
                var address = new byte[28];
                var family = ipv6 ? OperatingSystem.IsWindows() ? 23 : OperatingSystem.IsMacOS() ? 30 : 10 : 2;
                if (OperatingSystem.IsMacOS()) { address[0] = (byte)(ipv6 ? 28 : 16); address[1] = (byte)family; }
                else BitConverter.TryWriteBytes(address.AsSpan(0, 2), (ushort)family);
                address[2] = (byte)(endpoint.Port >> 8); address[3] = (byte)endpoint.Port;
                endpoint.Address.GetAddressBytes().CopyTo(address, ipv6 ? 8 : 4);
                if (ipv6) BitConverter.TryWriteBytes(address.AsSpan(24, 4), checked((uint)endpoint.Address.ScopeId));
                var pin = GCHandle.Alloc(alpn, GCHandleType.Pinned);
                var nativeAddress = IntPtr.Zero;
                var buffer = IntPtr.Zero;
                try
                {
                    nativeAddress = Marshal.AllocHGlobal(address.Length); Marshal.Copy(address, 0, nativeAddress, address.Length);
                    buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeBuffer>());
                    Marshal.StructureToPtr(new NativeBuffer { Length = (uint)alpn.Length, Bytes = pin.AddrOfPinnedObject() }, buffer, false);
                    var status = _start(listener, buffer, 1, nativeAddress);
                    if (status != 0) throw new IOException("MsQuic listener start failed with status 0x" + status.ToString("X8"));
                }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    if (nativeAddress != IntPtr.Zero) Marshal.FreeHGlobal(nativeAddress);
                    pin.Free();
                }
            }
            internal IPEndPoint LocalEndPoint(IntPtr listener)
            {
                var buffer = Marshal.AllocHGlobal(28);
                try
                {
                    uint size = 28;
                    var status = _get(listener, 0x04000000, ref size, buffer);
                    if (status != 0 || size > 28 || size < 16) throw new IOException("MsQuic listener address query failed.");
                    var address = new byte[28]; Marshal.Copy(buffer, address, 0, (int)size);
                    var family = OperatingSystem.IsMacOS() ? address[1] : BitConverter.ToUInt16(address, 0);
                    var port = (address[2] << 8) | address[3];
                    if (family == 2) return new IPEndPoint(new IPAddress(address.AsSpan(4, 4)), port);
                    var expected = OperatingSystem.IsWindows() ? 23 : OperatingSystem.IsMacOS() ? 30 : 10;
                    if (family != expected || size < 28) throw new IOException("MsQuic returned an invalid listener address family.");
                    return new IPEndPoint(new IPAddress(address.AsSpan(8, 16), BitConverter.ToUInt32(address, 24)), port);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        internal MsQuicNativeListener CreateListener(MsQuicRegistration registration)
        {
            var signals = new MsQuicNativeListener.Signals();
            var retained = false; registration.DangerousAddRef(ref retained);
            var listener = IntPtr.Zero;
            try
            {
                var status = _listenerFunctions.Open(registration.DangerousGetHandle(), signals.Handler, IntPtr.Zero, out listener);
                if (status != 0) throw new IOException("MsQuic listener creation failed with status 0x" + status.ToString("X8"));
                if (listener == IntPtr.Zero) throw new IOException("MsQuic returned an empty listener.");
                var result = new MsQuicNativeListener(listener, registration, _listenerFunctions, signals);
                listener = IntPtr.Zero; retained = false;
                return result;
            }
            finally
            {
                if (listener != IntPtr.Zero) _listenerFunctions.Close(listener);
                GC.KeepAlive(signals);
                if (retained) registration.DangerousRelease();
            }
        }
    }
    internal sealed class MsQuicNativeListener : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal sealed class Signals
        {
            internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly MsQuicApi.ListenerFunctions.Callback Handler;
            internal Signals() { Handler = OnEvent; }
            private uint OnEvent(IntPtr listener, IntPtr context, IntPtr eventData)
            {
                try
                {
                    if (Marshal.ReadInt32(eventData) == 1) Stopped.TrySetResult();
                    // Connection ownership is not implemented yet. Never accept a
                    // native handle without installing its callback and lifetime.
                    if (Marshal.ReadInt32(eventData) == 0)
                        return OperatingSystem.IsWindows() ? 0x80004004u : OperatingSystem.IsMacOS() ? 89u : 125u;
                    return 0;
                }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                {
                    Stopped.TrySetException(error);
                    return OperatingSystem.IsWindows() ? 0x80004004u : OperatingSystem.IsMacOS() ? 89u : 125u;
                }
            }
        }
        private readonly object _sync = new();
        private readonly MsQuicRegistration _registration;
        private readonly MsQuicApi.ListenerFunctions _functions;
        private readonly Signals _signals;
        private bool _started;
        private bool _stopping;
        internal MsQuicNativeListener(IntPtr listener, MsQuicRegistration registration, MsQuicApi.ListenerFunctions functions, Signals signals) : base(true)
        { _registration = registration; _functions = functions; _signals = signals; SetHandle(listener); }
        internal void Start(IPEndPoint endpoint, byte[] alpn)
        {
            lock (_sync)
            {
                if (_started) throw new InvalidOperationException("Listener already started.");
                var retained = false; DangerousAddRef(ref retained);
                try { _functions.Start(handle, endpoint, alpn); _started = true; }
                finally { if (retained) DangerousRelease(); }
            }
        }
        internal IPEndPoint LocalEndPoint()
        {
            var retained = false; DangerousAddRef(ref retained);
            try { return _functions.LocalEndPoint(handle); }
            finally { if (retained) DangerousRelease(); }
        }
        internal Task StopAsync()
        {
            lock (_sync)
            {
                if (_stopping) return _signals.Stopped.Task;
                var retained = false; DangerousAddRef(ref retained);
                try
                {
                    if (!_started) return Task.CompletedTask;
                    _stopping = true; _functions.Stop(handle);
                    return _signals.Stopped.Task;
                }
                finally { if (retained) DangerousRelease(); }
            }
        }
        protected override bool ReleaseHandle()
        {
            try { _functions.Close(handle); _signals.Stopped.TrySetResult(); GC.KeepAlive(_signals); }
            finally { _registration.DangerousRelease(); }
            return true;
        }
    }
}
#endif
