#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using EmbedIO.Internal;

namespace EmbedIO.Quic
{
    // Native code owns recordless TLS callbacks and bounded queues. This owner
    // publishes packet keys, validates application certificate policy and owns copies.
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed unsafe partial class OpenSslTls : TlsSession
    {
        private Func<string?, X509Certificate2?>? _selector;
        private readonly Func<X509Certificate2?, X509Certificate2Collection, bool>? _validateClientCertificate;
        private X509Certificate2? _clientCertificate;
        private readonly NativeHandle _owner;
        private Reassembly?[] _input = [null, null, null];
        private bool _disposed;
        private readonly List<string>? _diagnostics;
        private QuicCipherSuite? _cipher;

        internal OpenSslTls(
            Func<string?, X509Certificate2?> selector,
            byte[] parameters,
            string protocol = "h3",
            Func<X509Certificate2?, X509Certificate2Collection, bool>? validateClientCertificate = null,
            bool recordDiagnostics = false
        )
        {
            ArgumentNullException.ThrowIfNull(selector);
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(protocol);

            if (parameters.Length > 4096 || protocol.Length is < 1 or > 255)
                throw new ArgumentException("Invalid TLS protocol or transport parameters.");

            foreach (var character in protocol)
                if (character is (char)0 or > (char)127)
                    throw new ArgumentException("Invalid TLS protocol.", nameof(protocol));

            EnsureAvailable();

            _selector = selector;

            if (recordDiagnostics) _diagnostics = [];

            _validateClientCertificate = validateClientCertificate;
            var alpn = Encoding.ASCII.GetBytes(protocol);

            fixed (byte* protocols = alpn, transport = parameters)
                _owner = new NativeHandle(
                    NativeMethods.New(
                        protocols,
                        (nuint)alpn.Length,
                        transport,
                        (nuint)parameters.Length,
                        validateClientCertificate != null ? 1 : 0
                    )
                );

            if (_owner.IsInvalid)
            {
                _owner.Dispose();
                throw new InvalidOperationException("Native TLS allocation failed.");
            }
        }

        internal IReadOnlyList<string> Diagnostics => _diagnostics ?? [];

        internal static void EnsureAvailable()
        {
            if (
                NativeMethods.Abi() != 2
                || Marshal.PtrToStringUTF8(NativeMethods.Version()) != "OpenSSL 3.5.9 29 Sep 2026"
            )
                throw new InvalidOperationException("Unexpected native TLS ABI or version.");
        }

        internal static ulong ActiveContexts => NativeMethods.Connections();
        internal static ulong ActiveBlocks => NativeMethods.Blocks();

        internal override void Add(int level, ulong offset, ReadOnlySpan<byte> bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if ((uint)level > 2) throw new ArgumentOutOfRangeException(nameof(level));

            (_input[level] ??= new Reassembly(65536)).Add(offset, bytes);
        }

        internal override void Pump()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (var level = 0; level < 3; level++)
            {
                var bytes = _input[level]?.Consume() ?? [];

                if (bytes.Length == 0) continue;

                try
                {
                    fixed (byte* input = bytes)
                        if (NativeMethods.Add(_owner, level, input, (nuint)bytes.Length) != 1)
                            throw new InvalidDataException("Native TLS input exceeds its limits.");
                }

                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            Span<byte> name = stackalloc byte[255];

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = NativeMethods.Step(_owner);
                var alert = NativeMethods.Alert(_owner);

                if (alert != 0) AlertCode = checked((byte)alert);

                ReadEvents();

                if (result < 0) throw new InvalidDataException("Native TLS handshake failed.");

                if (result != 2)
                {
                    if (result == 1 && !Established)
                    {
                        if (ReadKeys[2] == null || WriteKeys[2] == null || PeerParameters == null)
                            throw Failure(80, "Incomplete native TLS handshake state.");

                        ValidateClientCertificate();
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        Established = true;
                    }

                    return;
                }

                int length;

                fixed (byte* data = name)
                    length = NativeMethods.ServerName(_owner, data, (nuint)name.Length);

                if (length < 0 || length > name.Length) throw Failure(47, "Invalid native server name.");

                foreach (var character in name[..length])
                    if (character == 0 || character > 127) throw Failure(47, "Invalid native server name.");

                var selector = _selector ?? throw Failure(80, "Native TLS requested a second certificate.");
                _selector = null;
                var serverName = length == 0 ? null : Encoding.ASCII.GetString(name[..length]);
                _diagnostics?.Add("server-name=" + (serverName ?? "(none)"));
                var certificate = selector(serverName);
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (certificate == null || !certificate.HasPrivateKey)
                    throw Failure(80, "The selected TLS certificate has no private key.");

                var credential = certificate.Export(X509ContentType.Pkcs12);

                try
                {
                    fixed (byte* input = credential)
                        if (NativeMethods.SetCredential(_owner, input, (nuint)credential.Length) != 1)
                            throw new InvalidDataException("Native TLS credential rejected.");

                    InstallServerChain(certificate);
                }

                finally
                {
                    CryptographicOperations.ZeroMemory(credential);
                }
            }

            throw new InvalidDataException("Native TLS requested more than one credential.");
        }

        internal override X509Certificate2? CopyClientCertificate() =>
            _clientCertificate == null ? null : X509CertificateLoader.LoadCertificate(_clientCertificate.RawData);

        private void InstallServerChain(X509Certificate2 certificate)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            // This chooses the configured server's outgoing chain, not peer trust.
            // Keep the runtime's normal issuer discovery behavior.
            try
            {
                _ = chain.Build(certificate);
                var incomplete = false;

                foreach (var status in chain.ChainStatus)
                    incomplete |= (status.Status & X509ChainStatusFlags.PartialChain) != 0;

                var end = chain.ChainElements.Count - (incomplete ? 0 : 1);

                for (var index = 1; index < end; index++)
                {
                    var encoded = chain.ChainElements[index].Certificate.RawData;

                    try
                    {
                        fixed (byte* input = encoded)
                            if (NativeMethods.AddChain(_owner, input, (nuint)encoded.Length) != 1)
                                throw Failure(80, "Native TLS issuer certificate rejected.");
                    }

                    finally
                    {
                        CryptographicOperations.ZeroMemory(encoded);
                    }
                }
            }

            finally
            {
                foreach (var element in chain.ChainElements)
                    if (!ReferenceEquals(element.Certificate, certificate)) element.Certificate.Dispose();
            }
        }

        private void ValidateClientCertificate()
        {
            var validate = _validateClientCertificate;

            if (validate == null) return;

            var additional = new X509Certificate2Collection();
            X509Certificate2? certificate = null;

            try
            {
                var count = NativeMethods.CertificateCount(_owner);

                if (count < 0 || count > 65537) throw Failure(80, "Invalid native certificate collection.");

                var remaining = 131072;

                for (var index = 0; index < count; index++)
                {
                    var length = NativeMethods.Certificate(_owner, index, null, 0);

                    if (length is < 1 or > 65536 || length > remaining)
                        throw Failure(80, "Invalid native certificate length.");

                    remaining -= length;
                    var encoded = new byte[length];

                    fixed (byte* output = encoded)
                        if (NativeMethods.Certificate(_owner, index, output, (nuint)encoded.Length) != length)
                            throw Failure(80, "Native certificate serialization failed.");

                    var copy = X509CertificateLoader.LoadCertificate(encoded);

                    if (index == 0) certificate = copy;

                    else additional.Add(copy);
                }

                if (!validate(certificate, additional))
                    throw Failure(
                        certificate == null ? (byte)116 : (byte)42,
                        "Client certificate policy refused the handshake."
                    );

                ObjectDisposedException.ThrowIf(_disposed, this);

                if (certificate != null)
                    _clientCertificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                throw Failure(42, "Client certificate validation failed.", error);
            }

            finally
            {
                certificate?.Dispose();

                foreach (var item in additional) item.Dispose();
            }
        }

        private InvalidDataException Failure(byte alert, string message, Exception? inner = null)
        {
            AlertCode ??= alert;
            return new InvalidDataException(message, inner);
        }

        private void ReadEvents()
        {
            Span<byte> buffer = stackalloc byte[4096];

            try
            {
                while (true)
                {
                    var result = Next(buffer, out var kind, out var level, out var cipher, out var length);

                    if (result == 0) return;

                    byte[]? large = null;

                    try
                    {
                        if (result == 2)
                        {
                            if (length <= (nuint)buffer.Length || length > 65536)
                                throw new InvalidDataException("Native TLS event exceeds its limits.");

                            large = new byte[(int)length];
                            result = Next(large, out kind, out level, out cipher, out length);
                        }

                        if (result != 1 || length > (nuint)(large?.Length ?? buffer.Length))
                            throw new InvalidDataException("Invalid native TLS event.");

                        ReadOnlySpan<byte> bytes = large == null ? buffer[..(int)length] : large.AsSpan(0, (int)length);

                        if (kind == 1)
                        {
                            if ((uint)level > 2 || Output.Count >= 32)
                                throw new InvalidDataException("Native TLS output exceeds its limits.");

                            Output.Enqueue((level, bytes.ToArray()));
                        }

                        else if (kind is 2 or 3)
                        {
                            var suite = (QuicCipherSuite)cipher;

                            if (
                                level is not (1 or 2)
                                || bytes.Length != PacketKeys.SecretLength(suite)
                                || (_cipher != null && _cipher != suite)
                            )
                                throw new InvalidDataException("Native TLS cipher or secret mismatch.");

                            _cipher = suite;
                            var keys = kind == 2 ? ReadKeys : WriteKeys;

                            if (keys[level] != null) throw new InvalidDataException("Duplicate native TLS secret.");

                            keys[level] = new PacketKeys(bytes, cipherSuite: suite);
                        }

                        else if (kind == 4)
                        {
                            if (PeerParameters != null || bytes.Length > 4096)
                                throw new InvalidDataException("Invalid native TLS transport parameters.");

                            PeerParameters = bytes.ToArray();
                        }

                        else throw new InvalidDataException("Unknown native TLS event.");
                    }

                    finally
                    {
                        CryptographicOperations.ZeroMemory(buffer);

                        if (large != null) CryptographicOperations.ZeroMemory(large);
                    }
                }
            }

            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        private int Next(Span<byte> bytes, out int kind, out int level, out int cipher, out nuint length)
        {
            fixed (byte* output = bytes)
                return NativeMethods.Next(
                    _owner,
                    out kind,
                    out level,
                    out cipher,
                    output,
                    (nuint)bytes.Length,
                    out length
                );
        }

        public override void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _owner.Dispose();
            _selector = null;
            _clientCertificate?.Dispose();
            _clientCertificate = null;
            _input = [];
            ReleaseKeys();
        }

        private sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal NativeHandle(nint pointer) : base(true) => SetHandle(pointer);

            protected override bool ReleaseHandle()
            {
                NativeMethods.Free(handle);
                return true;
            }
        }

        private static partial class NativeMethods
        {
            private const string Library = "embedio_quic_tls";

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_connections")]
            internal static partial ulong Connections();

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_blocks")]
            internal static partial ulong Blocks();

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_abi")]
            internal static partial uint Abi();

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_version")]
            internal static partial nint Version();

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_new")]
            internal static partial nint New(
                byte* alpn,
                nuint alpnLength,
                byte* parameters,
                nuint parametersLength,
                int clientCertificate
            );

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_free")]
            internal static partial void Free(nint owner);

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_add")]
            internal static partial int Add(NativeHandle owner, int level, byte* bytes, nuint length);

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_step")]
            internal static partial int Step(NativeHandle owner);

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_server_name")]
            internal static partial int ServerName(NativeHandle owner, byte* bytes, nuint capacity);

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_set_credential")]
            internal static partial int SetCredential(NativeHandle owner, byte* bytes, nuint length);

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_add_chain")]
            internal static partial int AddChain(NativeHandle owner, byte* bytes, nuint length);

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_next")]
            internal static partial int Next(
                NativeHandle owner,
                out int kind,
                out int level,
                out int cipher,
                byte* bytes,
                nuint capacity,
                out nuint length
            );

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_alert")]
            internal static partial int Alert(NativeHandle owner);

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_certificate_count")]
            internal static partial int CertificateCount(NativeHandle owner);

            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
            [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
            [LibraryImport(Library, EntryPoint = "embedio_quic_tls_certificate")]
            internal static partial int Certificate(NativeHandle owner, int index, byte* output, nuint capacity);
        }
    }
}
#endif
