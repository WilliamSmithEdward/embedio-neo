using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EmbedIO.Quic
{
    // Only OpenSSL's published third-party QUIC TLS API is used. This class owns
    // native handles and callback buffers; it contains no QUIC transport implementation.
    internal sealed class OpenSslTls : TlsSession
    {
        internal const string ApplicationProtocol = "embedio-quic-probe";
        private readonly NativeApi _api;
        private readonly string _protocol;
        private readonly nint _context;
        private readonly nint _ssl;
        private readonly nint _dispatch;
        private readonly nint _parameters;
        private readonly Delegate[] _callbacks;
        private nint _record;
        private int _readLevel;
        private int _writeLevel;
        private Exception? _callbackError;
        private Reassembly[] _input =
        [
            new(65536),
            new(65536),
            new(65536)
        ];
        private bool _disposed;

        internal OpenSslTls(
            string directory,
            string certificate,
            string privateKey,
            byte[] parameters,
            string protocol = ApplicationProtocol
        )
        {
            _protocol = protocol;
            _api = new NativeApi(directory);

            try
            {
                _context = _api.Get<NewPointer>("SSL_CTX_new")(_api.Get<PointerResult>("TLS_method")());
                Require(_context != 0, "TLS context");
                Require(
                    _api.Get<SetText>("SSL_CTX_set_ciphersuites")(_context, "TLS_AES_128_GCM_SHA256") == 1,
                    "cipher suite"
                );
                Require(
                    _api.Get<LoadFile>("SSL_CTX_use_certificate_file")(_context, certificate, 1) == 1,
                    "certificate"
                );
                Require(_api.Get<LoadFile>("SSL_CTX_use_PrivateKey_file")(_context, privateKey, 1) == 1, "private key");
                Require(_api.Get<IntResult>("SSL_CTX_check_private_key")(_context) == 1, "certificate key match");
                var alpn = new SelectAlpn(SelectProtocol);
                _api.Get<SetCallback>("SSL_CTX_set_alpn_select_cb")(
                    _context,
                    Marshal.GetFunctionPointerForDelegate(alpn),
                    0
                );
                _ssl = _api.Get<NewPointer>("SSL_new")(_context);
                Require(_ssl != 0, "TLS connection");
                _api.Get<VoidPointer>("SSL_set_accept_state")(_ssl);
                _callbacks =
                [
                    new SendCrypto(Send),
                    new ReceiveCrypto(Receive),
                    new ReleaseCrypto(Release),
                    new YieldSecret(Secret),
                    new ReceiveParameters(Parameters),
                    new AlertCallback(Alert),
                    alpn
                ];
                _dispatch = Marshal.AllocHGlobal(7 * 2 * nint.Size);

                for (var i = 0; i < 7; i++)
                {
                    Marshal.WriteIntPtr(_dispatch, i * 2 * nint.Size, i < 6 ? 2001 + i : 0);
                    Marshal.WriteIntPtr(
                        _dispatch,
                        (i * 2 + 1) * nint.Size,
                        i < 6 ? Marshal.GetFunctionPointerForDelegate(_callbacks[i]) : 0
                    );
                }

                Require(_api.Get<SetDispatch>("SSL_set_quic_tls_cbs")(_ssl, _dispatch, 0) == 1, "QUIC TLS callbacks");
                _parameters = Marshal.AllocHGlobal(parameters.Length);
                Marshal.Copy(parameters, 0, _parameters, parameters.Length);
                Require(
                    _api.Get<SetBytes>("SSL_set_quic_tls_transport_params")(_ssl, _parameters, (nuint)parameters.Length)
                        == 1,
                    "transport parameters"
                );
            }

            catch
            {
                Dispose();
                throw;
            }
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
            var result = _api.Get<IntResult>("SSL_do_handshake")(_ssl);

            if (_callbackError != null) throw new InvalidDataException("QUIC TLS callback failed.", _callbackError);

            if (result != 1 && _api.Get<GetError>("SSL_get_error")(_ssl, result) != 2)
                throw new InvalidDataException("QUIC TLS handshake failed: " + _api.Error());

            Established = _api.Get<IntResult>("SSL_is_init_finished")(_ssl) == 1;
        }

        private int Send(nint ssl, nint data, nuint length, out nuint consumed, nint argument)
        {
            consumed = 0;

            if (length > 65536 || Output.Count >= 32) return Fail("TLS output exceeds probe limits.");

            var bytes = new byte[(int)length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            Output.Enqueue((_writeLevel, bytes));
            consumed = length;
            return 1;
        }

        private int Receive(nint ssl, out nint data, out nuint length, nint argument)
        {
            data = 0;
            length = 0;

            if (_record != 0) return Fail("TLS retained two callback records.");

            var bytes = _input[_readLevel].Consume();

            if (bytes.Length == 0) return 1;

            _record = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, _record, bytes.Length);
            data = _record;
            length = (nuint)bytes.Length;
            return 1;
        }

        private int Release(nint ssl, nuint length, nint argument)
        {
            Marshal.FreeHGlobal(_record);
            _record = 0;
            return 1;
        }

        private int Secret(nint ssl, uint protection, int direction, nint data, nuint length, nint argument)
        {
            if ((protection != 2 && protection != 3) || (direction != 0 && direction != 1) || length != 32)
                return Fail("Unsupported TLS secret in probe.");

            var level = protection == 2 ? 1 : 2;
            var keys = direction == 0 ? ReadKeys : WriteKeys;

            if (keys[level] != null) return Fail("TLS secret replacement is outside this milestone.");

            var bytes = new byte[32];
            Marshal.Copy(data, bytes, 0, bytes.Length);

            try
            {
                keys[level] = new PacketKeys(bytes);
            }

            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            if (direction == 0) _readLevel = level;

            else _writeLevel = level;

            return 1;
        }

        private int Parameters(nint ssl, nint data, nuint length, nint argument)
        {
            if (length > 4096 || PeerParameters != null) return Fail("Invalid peer transport parameter callback.");

            PeerParameters = new byte[(int)length];
            Marshal.Copy(data, PeerParameters, 0, PeerParameters.Length);
            return 1;
        }

        private int Alert(nint ssl, byte code, nint argument)
        {
            AlertCode = code;
            return Fail("TLS alert " + code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private int Fail(string message)
        {
            _callbackError ??= new InvalidDataException(message);
            return 0;
        }

        private int SelectProtocol(
            nint ssl,
            out nint output,
            out byte outputLength,
            nint input,
            uint inputLength,
            nint argument
        )
        {
            output = 0;
            outputLength = 0;

            if (inputLength > 65535) return 2;

            var expected = Encoding.ASCII.GetBytes(_protocol);
            var offset = 0;

            while (offset < (int)inputLength)
            {
                var length = Marshal.ReadByte(input, offset++);

                if (length > (int)inputLength - offset) return 2;

                var candidate = new byte[length];
                Marshal.Copy(input + offset, candidate, 0, length);

                if (
                    candidate
                        .AsSpan()
                        .SequenceEqual(expected)
                )
                {
                    output = input + offset;
                    outputLength = length;
                    return 0;
                }

                offset += length;
            }

            return 2;
        }

        private void Require(bool success, string operation)
        {
            if (!success) throw new InvalidOperationException("OpenSSL " + operation + ": " + _api.Error());
        }

        public override void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            if (_ssl != 0) _api.Get<VoidPointer>("SSL_free")(_ssl);

            if (_context != 0) _api.Get<VoidPointer>("SSL_CTX_free")(_context);

            Marshal.FreeHGlobal(_record);
            Marshal.FreeHGlobal(_parameters);
            Marshal.FreeHGlobal(_dispatch);

            _input = [];
            ReleaseKeys();

            _api.Dispose();
            GC.KeepAlive(_callbacks);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint PointerResult();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint NewPointer(nint pointer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VoidPointer(nint pointer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IntResult(nint pointer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetText(nint pointer, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LoadFile(nint pointer, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetCallback(nint pointer, nint callback, nint argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetDispatch(nint pointer, nint dispatch, nint argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetBytes(nint pointer, nint bytes, nuint length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetError(nint pointer, int result);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SendCrypto(nint ssl, nint bytes, nuint length, out nuint consumed, nint argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReceiveCrypto(nint ssl, out nint bytes, out nuint length, nint argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReleaseCrypto(nint ssl, nuint length, nint argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int YieldSecret(
            nint ssl,
            uint protection,
            int direction,
            nint bytes,
            nuint length,
            nint argument
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReceiveParameters(nint ssl, nint bytes, nuint length, nint argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AlertCallback(nint ssl, byte code, nint argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SelectAlpn(
            nint ssl,
            out nint output,
            out byte outputLength,
            nint input,
            uint inputLength,
            nint argument
        );

        private sealed class NativeApi : IDisposable
        {
            private readonly nint _crypto;
            private readonly nint _ssl;
            private readonly Dictionary<string, Delegate> _functions = [];

            internal NativeApi(string directory)
            {
                if (!Path.IsPathFullyQualified(directory))
                    throw new ArgumentException("Supply an absolute OpenSSL library directory.", nameof(directory));

                var windows = OperatingSystem.IsWindows();
                _crypto = NativeLibrary.Load(
                    Path.Combine(directory, windows ? "libcrypto-3-x64.dll" : "libcrypto.so.3")
                );

                try
                {
                    _ssl = NativeLibrary.Load(Path.Combine(directory, windows ? "libssl-3-x64.dll" : "libssl.so.3"));
                    var version = Marshal.GetDelegateForFunctionPointer<VersionText>(
                        NativeLibrary.GetExport(_crypto, "OpenSSL_version")
                    )(0);
                    var name = Marshal.PtrToStringUTF8(version) ?? "unknown";

                    if (!name.StartsWith("OpenSSL 3.5.7 ", StringComparison.Ordinal))
                        throw new NotSupportedException("This probe is pinned to OpenSSL 3.5.7; loaded " + name);
                }

                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal T Get<T>(string name) where T : Delegate
            {
                if (_functions.TryGetValue(name, out var function)) return (T)function;

                var result = Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_ssl, name));
                _functions.Add(name, result);
                return result;
            }

            internal string Error()
            {
                var code = Marshal.GetDelegateForFunctionPointer<ErrorCode>(
                    NativeLibrary.GetExport(_crypto, "ERR_get_error")
                )();
                return "error code " + code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            public void Dispose()
            {
                if (_ssl != 0) NativeLibrary.Free(_ssl);

                if (_crypto != 0) NativeLibrary.Free(_crypto);
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate nint VersionText(int type);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate nuint ErrorCode();
        }
    }
}
