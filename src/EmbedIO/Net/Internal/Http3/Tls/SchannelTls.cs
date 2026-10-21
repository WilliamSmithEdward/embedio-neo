#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EmbedIO.Internal;

namespace EmbedIO.Quic
{
    // Original binding to the Windows SDK's public recordless SSPI interface.
    // The connection owner serializes native calls and owns the credential/context lease.
    [SupportedOSPlatform("windows")]
    internal sealed partial class SchannelTls : TlsSession
    {
        private const int Capacity = 65536;
        private X509Certificate2? _certificate;
        private Func<string?, X509Certificate2?>? _selector;
        private string? _serverName;
        private readonly string _protocol;
        private readonly bool _recordDiagnostics;
        private Reassembly[] _input =
        [
            new(Capacity),
            new(Capacity),
            new(Capacity)
        ];
        private byte[] _pending = new byte[Capacity];
        private readonly NativeRegion _context = new(2 * nint.Size);
        private readonly NativeRegion _incoming = new(Capacity);
        private readonly NativeRegion _outgoing = new(Capacity);
        private readonly NativeRegion _alert = new(2);
        private readonly NativeRegion[] _secrets =
        [
            new(512),
            new(512),
            new(512),
            new(512)
        ];
        private readonly NativeRegion _flags = new(8);
        private readonly NativeRegion _alpn;
        private readonly NativeRegion _parameters;
        private NativePair _credential;
        private bool _credentialOwned;
        private bool _contextOwned;
        private static int _activeCredentials;
        private static int _activeContexts;
        private static int _activeRegions;

        internal static int ActiveCredentials => Volatile.Read(ref _activeCredentials);
        internal static int ActiveContexts => Volatile.Read(ref _activeContexts);
        internal static int ActiveRegions => Volatile.Read(ref _activeRegions);

        private bool _disposed;
        private bool _started;
        private int _count;
        private int _readLevel;
        private readonly byte[]?[] _readSecrets = [null, null, null];
        private readonly byte[]?[] _writeSecrets = [null, null, null];

        internal List<string> Diagnostics { get; } = [];
        internal string? SelectedCertificateSha256 => _certificate?.GetCertHashString(HashAlgorithmName.SHA256);
        internal bool NativeContextCreated => _contextOwned;

        internal SchannelTls(
            X509Certificate2 certificate,
            byte[] parameters,
            string protocol,
            bool recordDiagnostics = true
        ) : this(FixedCertificate(certificate), parameters, protocol, recordDiagnostics)
        { }

        private static Func<string?, X509Certificate2> FixedCertificate(X509Certificate2 certificate)
        {
            ArgumentNullException.ThrowIfNull(certificate);

            if (!certificate.HasPrivateKey)
                throw new ArgumentException("The TLS certificate has no private key.", nameof(certificate));

            return _ => certificate;
        }

        internal SchannelTls(
            Func<string?, X509Certificate2?> selector,
            byte[] parameters,
            string protocol,
            bool recordDiagnostics = true
        )
        {
            _protocol = protocol;
            _recordDiagnostics = recordDiagnostics;

            try
            {
                ArgumentNullException.ThrowIfNull(selector);
                ArgumentNullException.ThrowIfNull(parameters);
                ArgumentNullException.ThrowIfNull(protocol);

                if (
                    parameters.Length > 4096
                    || protocol.Length is < 1 or > 255 || protocol.Any(character => character > 127 || character == 0)
                )
                    throw new ArgumentException("Invalid TLS credential, protocol or transport parameters.");

                _selector = selector;
                _alpn = new NativeRegion(11 + protocol.Length);
                // SEND_GENERIC_TLS_EXTENSION includes a one-byte trailing array
                // and alignment padding even when BufferSize is zero.
                _parameters = new NativeRegion(Math.Max(12, 10 + parameters.Length));
                var alpn = _alpn.Bytes;
                BinaryPrimitives.WriteUInt32LittleEndian(alpn, (uint)(alpn.Length - 4));
                BinaryPrimitives.WriteUInt32LittleEndian(alpn[4..], 2);
                BinaryPrimitives.WriteUInt16LittleEndian(alpn[8..], (ushort)(protocol.Length + 1));
                alpn[10] = (byte)protocol.Length;
                Encoding.ASCII.GetBytes(protocol, alpn[11..]);
                var extension = _parameters.Bytes;
                BinaryPrimitives.WriteUInt16LittleEndian(extension, 57);
                BinaryPrimitives.WriteUInt16LittleEndian(extension[2..], 8);
                BinaryPrimitives.WriteUInt16LittleEndian(extension[8..], (ushort)parameters.Length);
                parameters.CopyTo(extension[10..]);
                BinaryPrimitives.WriteUInt64LittleEndian(_flags.Bytes, 1UL << 32);
            }

            catch
            {
                Dispose();
                throw;
            }
        }

        private void AcquireCredential()
        {
            using var certificate = new NativeRegion(nint.Size);
            var selected = _certificate ?? throw new InvalidOperationException("TLS credential has not been selected.");
            Marshal.WriteIntPtr(certificate.Address, selected.Handle);
            // This development profile currently supports AES-128-GCM/SHA-256.
            // Native cipher selection must match the existing packet-key profile.
            using var aesName = NativeRegion.Text("AES");
            using var chachaName = NativeRegion.Text("CHACHA20_POLY1305");
            using var blocked = new NativeRegion(2 * Marshal.SizeOf<AlgorithmRestriction>());
            blocked.Structure(0, new AlgorithmRestriction
            {
                Usage = 2,
                Algorithm = aesName.UnicodeName,
                MaximumBits = 128
            });
            blocked.Structure(Marshal.SizeOf<AlgorithmRestriction>(), new AlgorithmRestriction
            {
                Usage = 2,
                Algorithm = chachaName.UnicodeName
            });
            using var settings = new NativeRegion(Marshal.SizeOf<ProtocolRestriction>());
            settings.Structure(0, new ProtocolRestriction
            {
                DisabledProtocols = ~0x3000U,
                AlgorithmCount = 2,
                Algorithms = blocked.Address
            });
            var input = new CredentialInput
            {
                Version = 5,
                CertificateCount = 1,
                Certificates = certificate.Address,
                Flags = 0x80,
                ParameterCount = 1,
                Parameters = settings.Address
            };
            var status = NativeMethods.Acquire(
                0,
                "Microsoft Unified Security Protocol Provider",
                1,
                0,
                ref input,
                0,
                0,
                out _credential,
                out _
            );

            if (status != 0) throw new InvalidOperationException($"TLS credential admission failed: {status:X8}");

            _credentialOwned = true;
            Interlocked.Increment(ref _activeCredentials);
        }

        internal override void Add(int level, ulong offset, ReadOnlySpan<byte> bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if ((uint)level > 2) throw new ArgumentOutOfRangeException(nameof(level));

            _input[level].Add(offset, bytes);
        }

        internal override void Pump()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (var turn = 0; turn < 8; turn++)
            {
                var incoming = _input[_readLevel].Consume();

                if (incoming.Length > Capacity - _count) throw Failure(80, "TLS input exceeds its limit.");

                incoming.CopyTo(_pending, _count);
                _count += incoming.Length;

                if (_count == 0) return;

                if (_readLevel == 0)
                {
                    if (_count < 4) return;

                    var size = (_pending[1] << 16) | (_pending[2] << 8) | _pending[3];

                    if (size > Capacity - 4) throw Failure(50, "Oversized ClientHello.");

                    if (_count < size + 4) return;

                    var hello = TlsClientHello.Read(_pending.AsSpan(0, size + 4));
                    var parameters = hello.TransportParameters;

                    if (_credentialOwned && !StringComparer.Ordinal.Equals(_serverName, hello.ServerName))
                        throw Failure(47, "TLS server name changed during retry.");

                    if (
                        PeerParameters != null
                        && !parameters
                            .AsSpan()
                            .SequenceEqual(PeerParameters)
                    )
                        throw Failure(47, "Client transport parameters changed during retry.");

                    PeerParameters ??= parameters;

                    if (!_credentialOwned)
                    {
                        var selector = _selector ?? throw Failure(80, "Missing TLS certificate selector.");
                        _selector = null;

                        try
                        {
                            var selected = selector(hello.ServerName);
                            ObjectDisposedException.ThrowIf(_disposed, this);

                            if (selected == null || !selected.HasPrivateKey)
                                throw Failure(80, "Invalid selected TLS credential.");

                            _certificate = new X509Certificate2(selected);
                            _serverName = hello.ServerName;

                            if (_recordDiagnostics && Diagnostics.Count < 64)
                                Diagnostics.Add("server-name=" + (hello.ServerName ?? "(none)"));

                            AcquireCredential();
                        }

                        catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                        {
                            throw Failure(80, "TLS credential preparation failed.", error);
                        }
                    }
                }

                var previousCount = _count;
                var status = Step();

                if (status == unchecked((int)0x80090318)) return;

                if (status < 0) throw Failure(AlertCode ?? 80, $"TLS handshake failed: {status:X8}");

                if (status is not (0 or 0x90312 or 0x90366)) throw Failure(80, "Unexpected TLS continuation status.");

                _started = true;

                if (status == 0)
                {
                    ConfirmProtocol();
                    Established = true;
                }

                _readLevel = Established ? 2 : ReadKeys[1] != null ? 1 : 0;

                if (_count == 0 && _input[_readLevel].Contiguous == 0) return;

                if (_count == previousCount && incoming.Length == 0) return;
            }
        }

        private int Step()
        {
            _pending
                .AsSpan(0, _count)
                .CopyTo(_incoming.Bytes);
            using var inputs = new NativeBuffers(_started ? 4 : 6);
            inputs.Set(0, 2, _incoming, _count);
            inputs.Empty(1);
            inputs.Empty(2);
            inputs.Set(3, 27, _flags);

            if (!_started)
            {
                inputs.Set(4, 18, _alpn);
                inputs.Set(5, 25, _parameters);
            }

            using var outputs = new NativeBuffers(6);
            outputs.Set(0, 2, _outgoing);
            outputs.Set(1, 17, _alert);

            for (var i = 0; i < _secrets.Length; i++)
            {
                _secrets[i].Bytes.Clear();
                outputs.Set(i + 2, 28, _secrets[i]);
            }

            _alert.Bytes.Clear();
            var inputDescriptor = inputs.Descriptor;
            var outputDescriptor = outputs.Descriptor;
            var oldContext = ContextExists ? _context.Address : 0;
            var status = NativeMethods.Accept(
                ref _credential,
                oldContext,
                ref inputDescriptor,
                0x18018,
                0,
                _context.Address,
                ref outputDescriptor,
                out _,
                out _
            );

            if (ContextExists && !_contextOwned)
            {
                _contextOwned = true;
                Interlocked.Increment(ref _activeContexts);
            }

            for (var i = 0; i < outputs.Count; i++)
            {
                var slot = outputs.Get(i);
                var region = i == 0 ? _outgoing : i == 1 ? _alert : _secrets[i - 2];

                if (slot.Address != region.Address || slot.Length > region.Length)
                    throw Failure(80, "Invalid native TLS output buffer.");
            }

            if (_recordDiagnostics && Diagnostics.Count < 64)
                Diagnostics.Add($"status={status:X8};input={_count};output={outputs.Get(0).Length}");

            var alert = outputs.Get(1);

            if (alert.Type == 17 && alert.Length >= 2 && _alert.Bytes[0] is 1 or 2) AlertCode = _alert.Bytes[1];

            // Failed SSPI calls may leave output lengths at their input capacities.
            // Only successful continuations publish tokens or traffic secrets.
            if (status < 0)
            {
                foreach (var secret in _secrets) secret.Bytes.Clear();

                return status;
            }

            var remaining = 0;

            for (var i = 0; i < inputs.Count; i++)
            {
                var slot = inputs.Get(i);

                if (slot.Type == 5)
                {
                    if (slot.Length > _count || remaining != 0) throw Failure(80, "Invalid TLS remainder.");

                    remaining = checked((int)slot.Length);
                }
            }

            _pending
                .AsSpan(_count - remaining, remaining)
                .CopyTo(_pending);
            _pending
                .AsSpan(remaining, _count - remaining)
                .Clear();
            _count = remaining;

            try
            {
                for (var i = 0; i < _secrets.Length; i++)
                {
                    var slot = outputs.Get(i + 2);

                    if (slot.Type == 28)
                    {
                        if (slot.Length < 430) throw Failure(80, "Truncated native traffic-secret buffer.");

                        InstallSecret(_secrets[i].Bytes);
                    }
                }
            }

            finally
            {
                foreach (var secret in _secrets) secret.Bytes.Clear();
            }

            var token = outputs.Get(0);

            if (token.Type == 2 && token.Length != 0)
            {
                if (token.Length > Capacity) throw Failure(80, "Oversized TLS output.");

                QueueMessages(_outgoing.Bytes[..(int)token.Length]);
            }

            return status;
        }

        private void InstallSecret(ReadOnlySpan<byte> secret)
        {
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(secret[392..]);

            if (_recordDiagnostics && kind != 0 && Diagnostics.Count < 64)
                Diagnostics.Add(
                    $"native-secret={kind};algorithm={Name(secret[..128])};mode={Name(secret.Slice(128, 128))};hash={Name(secret.Slice(256, 128))};key={BinaryPrimitives.ReadUInt16LittleEndian(secret[384..])};iv={BinaryPrimitives.ReadUInt16LittleEndian(secret[386..])};size={BinaryPrimitives.ReadUInt16LittleEndian(secret[396..])}"
                );

            if (kind == 0) return;

            if (
                kind is not (1 or 2)
                || Name(secret[..128]) != "AES"
                || Name(secret.Slice(128, 128)) != "ChainingModeGCM"
                || Name(secret.Slice(256, 128)) != "SHA256"
                || BinaryPrimitives.ReadUInt16LittleEndian(secret[384..]) != 16
                || BinaryPrimitives.ReadUInt16LittleEndian(secret[386..]) != 12
                || BinaryPrimitives.ReadUInt16LittleEndian(secret[396..]) != 32
            )
                throw Failure(40, "Unsupported TLS traffic-secret profile.");

            var keys = kind == 1 ? ReadKeys : WriteKeys;
            var seen = kind == 1 ? _readSecrets : _writeSecrets;
            var value = secret.Slice(398, 32);

            foreach (var previous in seen)
                if (previous != null && CryptographicOperations.FixedTimeEquals(previous, value)) return;

            var level = keys[1] == null ? 1 : keys[2] == null ? 2 : 0;

            if (level == 0) throw Failure(10, "Unexpected replacement TLS secret.");

            keys[level] = new PacketKeys(value);
            seen[level] = value.ToArray();

            if (_recordDiagnostics && Diagnostics.Count < 64) Diagnostics.Add($"secret-direction={kind};level={level}");
        }

        private static string Name(ReadOnlySpan<byte> bytes) =>
            Encoding.Unicode
                .GetString(bytes)
                .TrimEnd('\0');

        private void QueueMessages(ReadOnlySpan<byte> token)
        {
            while (!token.IsEmpty)
            {
                if (token.Length < 4) throw Failure(80, "Truncated TLS output message.");

                var size = (token[1] << 16) | (token[2] << 8) | token[3];

                if (size > token.Length - 4) throw Failure(80, "Invalid TLS output boundary.");

                var level = token[0] switch
                {
                    2 => 0,
                    8 or 11 or 13 or 15 or 20 => 1,
                    4 => 2,
                    _ => throw Failure(10, "Unsupported TLS handshake output.")
                };

                if (Output.Count >= 32) throw Failure(80, "TLS output queue is full.");

                Output.Enqueue((level, token[..(size + 4)].ToArray()));
                token = token[(size + 4)..];
            }
        }

        private void ConfirmProtocol()
        {
            using var output = new NativeRegion(264);

            if (NativeMethods.Query(_context.Address, 35, output.Address) != 0)
                throw Failure(120, "Missing negotiated ALPN.");

            var bytes = output.Bytes;
            var count = bytes[8];

            if (
                BinaryPrimitives.ReadInt32LittleEndian(bytes) != 1
                || !bytes
                    .Slice(9, count)
                    .SequenceEqual(Encoding.ASCII.GetBytes(_protocol))
            )
                throw Failure(120, "Unexpected negotiated ALPN.");

            if (ReadKeys[2] == null || WriteKeys[2] == null || PeerParameters == null)
                throw Failure(80, "Incomplete TLS handshake state.");
        }

        private InvalidDataException Failure(byte alert, string message, Exception? inner = null)
        {
            if (_recordDiagnostics && Diagnostics.Count < 64) Diagnostics.Add(message);

            AlertCode ??= alert;
            return new InvalidDataException(message, inner);
        }

        private bool ContextExists
        {
            get
            {
                var first = Marshal.ReadIntPtr(_context.Address);
                var second = Marshal.ReadIntPtr(_context.Address, nint.Size);
                return (first != 0 || second != 0) && (first != -1 || second != -1);
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            var contextStatus = ContextExists ? NativeMethods.Delete(_context.Address) : 0;

            if (_contextOwned && contextStatus == 0)
            {
                _contextOwned = false;
                Interlocked.Decrement(ref _activeContexts);
            }

            var credentialStatus = _credentialOwned ? NativeMethods.Free(ref _credential) : 0;

            if (_credentialOwned && credentialStatus == 0)
            {
                _credentialOwned = false;
                Interlocked.Decrement(ref _activeCredentials);
            }

            _selector = null;
            _certificate?.Dispose();
            _certificate = null;
            _context.Dispose();
            _incoming.Dispose();
            _outgoing.Dispose();
            _alert.Dispose();
            _flags.Dispose();
            _alpn?.Dispose();
            _parameters?.Dispose();

            foreach (var secret in _secrets) secret.Dispose();

            foreach (var secret in _readSecrets.Concat(_writeSecrets))
                if (secret != null) CryptographicOperations.ZeroMemory(secret);

            CryptographicOperations.ZeroMemory(_pending);
            _pending = [];
            _input = [];
            Array.Clear(_readSecrets);
            Array.Clear(_writeSecrets);
            ReleaseKeys();

            if (contextStatus != 0 || credentialStatus != 0)
                throw new InvalidOperationException(
                    $"Native TLS cleanup failed: {contextStatus:X8}/{credentialStatus:X8}"
                );
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePair
        {
            internal nint First;
            internal nint Second;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CredentialInput
        {
            internal uint Version;
            internal uint Format;
            internal uint CertificateCount;
            internal nint Certificates;
            internal nint Roots;
            internal uint MapperCount;
            internal nint Mappers;
            internal uint SessionLifetime;
            internal uint Flags;
            internal uint ParameterCount;
            internal nint Parameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeName
        {
            internal ushort Length;
            internal ushort Capacity;
            internal nint Text;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AlgorithmRestriction
        {
            internal int Usage;
            internal UnicodeName Algorithm;
            internal uint ModeCount;
            internal nint Modes;
            internal uint MinimumBits;
            internal uint MaximumBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProtocolRestriction
        {
            internal uint ProtocolCount;
            internal nint Protocols;
            internal uint DisabledProtocols;
            internal uint AlgorithmCount;
            internal nint Algorithms;
            internal uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeBuffer
        {
            internal uint Length;
            internal uint Type;
            internal nint Address;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BufferList
        {
            internal uint Version;
            internal uint Count;
            internal nint Buffers;
        }

        private sealed class NativeBuffers : IDisposable
        {
            private readonly NativeRegion _memory;

            internal NativeBuffers(int count)
            {
                _memory = new NativeRegion(count * Marshal.SizeOf<NativeBuffer>());
                Count = count;
            }

            internal int Count { get; }
            internal BufferList Descriptor => new()
            {
                Count = (uint)Count,
                Buffers = _memory.Address
            };

            internal void Empty(int index) =>
                _memory.Structure(index * Marshal.SizeOf<NativeBuffer>(), new NativeBuffer());

            internal void Set(int index, uint type, NativeRegion region, int? count = null) =>
                _memory.Structure(index * Marshal.SizeOf<NativeBuffer>(), new NativeBuffer
                {
                    Length = (uint)(count ?? region.Length),
                    Type = type,
                    Address = region.Address
                });

            internal NativeBuffer Get(int index) =>
                Marshal.PtrToStructure<NativeBuffer>(_memory.Address + index * Marshal.SizeOf<NativeBuffer>());

            public void Dispose() => _memory.Dispose();
        }

        private sealed unsafe class NativeRegion : SafeHandle
        {
            internal NativeRegion(int length) : base(0, true)
            {
                Length = length;
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
                SetHandle(Marshal.AllocHGlobal(length));
                Interlocked.Increment(ref _activeRegions);
                Bytes.Clear();
            }

            internal int Length { get; }
            public override bool IsInvalid => handle == 0;

            internal nint Address
            {
                get
                {
                    ObjectDisposedException.ThrowIf(IsClosed, this);
                    return handle;
                }
            }

            internal Span<byte> Bytes => new((void*)Address, Length);
            internal UnicodeName UnicodeName => new()
            {
                Length = (ushort)(Length - 2),
                Capacity = (ushort)Length,
                Text = Address
            };

            internal static NativeRegion Text(string value)
            {
                var result = new NativeRegion(2 * (value.Length + 1));
                Encoding.Unicode.GetBytes(value, result.Bytes);
                return result;
            }

            internal void Structure<T>(int offset, T value) where T : struct =>
                Marshal.StructureToPtr(value, Address + offset, false);

            protected override bool ReleaseHandle()
            {
                CryptographicOperations.ZeroMemory(new Span<byte>((void*)handle, Length));
                Marshal.FreeHGlobal(handle);
                Interlocked.Decrement(ref _activeRegions);
                return true;
            }
        }

        private static partial class NativeMethods
        {
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [LibraryImport(
                "secur32.dll",
                EntryPoint = "AcquireCredentialsHandleW",
                StringMarshalling = StringMarshalling.Utf16
            )]
            internal static partial int Acquire(
                nint principal,
                string package,
                uint direction,
                nint identifier,
                ref CredentialInput input,
                nint keyCallback,
                nint callbackState,
                out NativePair credential,
                out long expiry
            );

            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [LibraryImport("secur32.dll", EntryPoint = "FreeCredentialsHandle")]
            internal static partial int Free(ref NativePair credential);

            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [LibraryImport("secur32.dll", EntryPoint = "AcceptSecurityContext")]
            internal static partial int Accept(
                ref NativePair credential,
                nint context,
                ref BufferList input,
                uint flags,
                uint representation,
                nint nextContext,
                ref BufferList output,
                out uint attributes,
                out long expiry
            );

            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [LibraryImport("secur32.dll", EntryPoint = "DeleteSecurityContext")]
            internal static partial int Delete(nint context);

            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [LibraryImport("secur32.dll", EntryPoint = "QueryContextAttributesW")]
            internal static partial int Query(nint context, uint attribute, nint output);
        }
    }
}
#endif
