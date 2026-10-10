#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Net.Quic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace EmbedIO.Net.Internal.Http3
{
    // Original managed binding to the public, stable MsQuic API v2. A child
    // handle holds a SafeHandle reference to its API table and loaded library.
    internal sealed partial class MsQuicApi : SafeHandleZeroOrMinusOneIsInvalid
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint OpenApi(uint version, out IntPtr table);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseApi(IntPtr table);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint OpenRegistration(IntPtr configuration, out IntPtr registration);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void CloseRegistration(IntPtr registration);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint OpenConfiguration(IntPtr registration, IntPtr alpn, uint count, IntPtr settings, uint settingsSize, IntPtr context, out IntPtr configuration);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void CloseConfiguration(IntPtr configuration);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeBuffer
        {
            internal uint Length;
            internal IntPtr Bytes;
        }
        // Public QUIC_SETTINGS v2 ABI: only these IsSet fields are supplied.
        // Unselected fields remain zero and inherit MsQuic's configuration defaults.
        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private struct StreamSettings
        {
            [FieldOffset(0)] internal ulong IsSet;
            [FieldOffset(94)] internal ushort PeerBidirectional;
            [FieldOffset(96)] internal ushort PeerUnidirectional;
            [FieldOffset(106)] internal byte BooleanFlags;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint LoadCredential(IntPtr configuration, IntPtr credentials);
        [StructLayout(LayoutKind.Sequential)]
        private struct Credential
        {
            internal uint Type;
            internal uint Flags;
            internal IntPtr Certificate;
            internal IntPtr Principal;
            internal IntPtr Reserved;
            internal IntPtr AsyncHandler;
            internal uint AllowedCipherSuites;
            internal IntPtr CaCertificateFile;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Pkcs12
        {
            internal IntPtr Bytes;
            internal uint Length;
            internal IntPtr Password;
        }
        private readonly LoadCredential _loadCredential;
        private readonly OpenConfiguration _configurationOpen;
        private readonly CloseConfiguration _configurationClose;
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
            _configurationOpen = Marshal.GetDelegateForFunctionPointer<OpenConfiguration>(Marshal.ReadIntPtr(table, 8 * IntPtr.Size));
            _configurationClose = Marshal.GetDelegateForFunctionPointer<CloseConfiguration>(Marshal.ReadIntPtr(table, 9 * IntPtr.Size));
            _loadCredential = Marshal.GetDelegateForFunctionPointer<LoadCredential>(Marshal.ReadIntPtr(table, 10 * IntPtr.Size));
            _listenerFunctions = new ListenerFunctions(table);
            _streamFunctions = new StreamFunctions(table);
            _connectionFunctions = new ConnectionFunctions(table, _streamFunctions);
            SetHandle(table);
        }

        internal static bool Failed(uint status) => OperatingSystem.IsWindows() ? unchecked((int)status) < 0 : unchecked((int)status) > 0;

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
                if (Failed(status)) throw new IOException("MsQuic API initialization failed with status 0x" + status.ToString("X8"));
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
                if (Failed(status)) throw new IOException("MsQuic registration failed with status 0x" + status.ToString("X8"));
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

        internal MsQuicConfiguration CreateConfiguration(MsQuicRegistration registration, byte[] alpn, ushort bidirectional = 0, ushort unidirectional = 0, bool sendBuffering = true)
        {
            if (alpn == null) throw new ArgumentNullException(nameof(alpn));
            if (alpn.Length == 0 || alpn.Length > 255) throw new ArgumentOutOfRangeException(nameof(alpn));
            var retained = false;
            registration.DangerousAddRef(ref retained);
            var pin = default(GCHandle);
            var buffer = IntPtr.Zero;
            var configuration = IntPtr.Zero;
            var settings = IntPtr.Zero;
            try
            {
                pin = GCHandle.Alloc(alpn, GCHandleType.Pinned);
                buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeBuffer>());
                Marshal.StructureToPtr(new NativeBuffer { Length = (uint)alpn.Length, Bytes = pin.AddrOfPinnedObject() }, buffer, false);
                settings = Marshal.AllocHGlobal(Marshal.SizeOf<StreamSettings>());
                Marshal.StructureToPtr(new StreamSettings
                {
                    IsSet = (1UL << 18) | (1UL << 19) | (sendBuffering ? 0 : 1UL << 24),
                    PeerBidirectional = bidirectional,
                    PeerUnidirectional = unidirectional,
                    BooleanFlags = 0,
                }, settings, false);
                var status = _configurationOpen(registration.DangerousGetHandle(), buffer, 1, settings, (uint)Marshal.SizeOf<StreamSettings>(), IntPtr.Zero, out configuration);
                if (Failed(status)) throw new IOException("MsQuic configuration failed with status 0x" + status.ToString("X8"));
                if (configuration == IntPtr.Zero) throw new IOException("MsQuic returned an empty configuration.");
                var result = new MsQuicConfiguration(configuration, registration, _configurationClose);
                configuration = IntPtr.Zero;
                retained = false;
                return result;
            }
            finally
            {
                if (configuration != IntPtr.Zero) _configurationClose(configuration);
                if (settings != IntPtr.Zero) Marshal.FreeHGlobal(settings);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (pin.IsAllocated) pin.Free();
                if (retained) registration.DangerousRelease();
            }
        }

        internal void LoadServerCertificate(MsQuicConfiguration configuration, X509Certificate2 certificate)
        {
            if (certificate == null) throw new ArgumentNullException(nameof(certificate));
            if (!certificate.HasPrivateKey) throw new ArgumentException("A server certificate requires its private key.", nameof(certificate));
            var retained = false;
            configuration.DangerousAddRef(ref retained);
            var pin = default(GCHandle);
            byte[]? keyBytes = null;
            var pkcs = IntPtr.Zero;
            var credentials = IntPtr.Zero;
            try
            {
                using var snapshot = new X509Certificate2(certificate);
                var input = new Credential();
                if (OperatingSystem.IsWindows())
                {
                    input.Type = 3; // Public CERT_CONTEXT credential form.
                    input.Certificate = snapshot.Handle;
                }
                else
                {
                    keyBytes = snapshot.Export(X509ContentType.Pkcs12);
                    pin = GCHandle.Alloc(keyBytes, GCHandleType.Pinned);
                    pkcs = Marshal.AllocHGlobal(Marshal.SizeOf<Pkcs12>());
                    Marshal.StructureToPtr(new Pkcs12 { Bytes = pin.AddrOfPinnedObject(), Length = (uint)keyBytes.Length }, pkcs, false);
                    input.Type = 6;
                    input.Certificate = pkcs;
                }
                // Flags zero selects synchronous server loading with no client
                // certificate requirement; peer validation is never disabled.
                credentials = Marshal.AllocHGlobal(Marshal.SizeOf<Credential>());
                Marshal.StructureToPtr(input, credentials, false);
                var status = _loadCredential(configuration.DangerousGetHandle(), credentials);
                if (Failed(status)) throw new IOException("MsQuic server credential loading failed with status 0x" + status.ToString("X8"));
            }
            finally
            {
                if (credentials != IntPtr.Zero) Marshal.FreeHGlobal(credentials);
                if (pkcs != IntPtr.Zero) Marshal.FreeHGlobal(pkcs);
                if (keyBytes != null) CryptographicOperations.ZeroMemory(keyBytes);
                if (pin.IsAllocated) pin.Free();
                if (retained) configuration.DangerousRelease();
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
        internal MsQuicNativeConnection AcceptConnection(IntPtr connection) => _api.AcceptConnection(this, connection);
        internal MsQuicNativeListener CreateListener() => _api.CreateListener(this);
        internal MsQuicConfiguration CreateConfiguration(byte[] alpn) => _api.CreateConfiguration(this, alpn);
        internal MsQuicConfiguration CreateStreamConfiguration(byte[] alpn, ushort bidirectional, ushort unidirectional)
            => _api.CreateConfiguration(this, alpn, bidirectional, unidirectional);
        internal MsQuicConfiguration CreateUnbufferedStreamConfiguration(byte[] alpn, ushort bidirectional, ushort unidirectional)
            => _api.CreateConfiguration(this, alpn, bidirectional, unidirectional, false);
        internal void LoadServerCertificate(MsQuicConfiguration configuration, X509Certificate2 certificate)
            => _api.LoadServerCertificate(configuration, certificate);
        protected override bool ReleaseHandle()
        {
            try { _close(handle); }
            finally { _api.DangerousRelease(); }
            return true;
        }
    }
    internal sealed class MsQuicConfiguration : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly object _credentialsSync = new();
        private bool _credentialsLoaded;
        internal MsQuicRegistration Registration => _registration;
        private readonly MsQuicRegistration _registration;
        private readonly MsQuicApi.CloseConfiguration _close;
        internal MsQuicConfiguration(IntPtr configuration, MsQuicRegistration registration, MsQuicApi.CloseConfiguration close) : base(true)
        { _registration = registration; _close = close; SetHandle(configuration); }
        internal void LoadServerCertificate(X509Certificate2 certificate)
        {
            lock (_credentialsSync)
            {
                if (_credentialsLoaded) throw new InvalidOperationException("Credentials are already loaded.");
                _registration.LoadServerCertificate(this, certificate);
                _credentialsLoaded = true;
            }
        }
        protected override bool ReleaseHandle()
        {
            try { _close(handle); }
            finally { _registration.DangerousRelease(); }
            return true;
        }
    }
}
#endif
