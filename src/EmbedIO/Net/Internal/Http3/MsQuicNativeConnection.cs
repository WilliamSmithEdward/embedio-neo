#if NET10_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
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
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint ShutdownStream(IntPtr stream, uint flags, ulong code);
            internal readonly SetCallback SetHandler;
            internal readonly CloseConnection Close;
            internal readonly ShutdownConnection Shutdown;
            internal readonly SetConfiguration Configure;
            internal readonly CloseStream StreamClose;
            internal readonly ShutdownStream StreamShutdown;
            internal readonly StreamFunctions Streams;
            internal readonly DatagramFunctions Datagrams;
            internal ConnectionFunctions(IntPtr table, StreamFunctions streams)
            {
                Streams = streams;
                Datagrams = new DatagramFunctions(table);
                SetHandler = Marshal.GetDelegateForFunctionPointer<SetCallback>(Marshal.ReadIntPtr(table, 2 * IntPtr.Size));
                Close = Marshal.GetDelegateForFunctionPointer<CloseConnection>(Marshal.ReadIntPtr(table, 16 * IntPtr.Size));
                Shutdown = Marshal.GetDelegateForFunctionPointer<ShutdownConnection>(Marshal.ReadIntPtr(table, 17 * IntPtr.Size));
                Configure = Marshal.GetDelegateForFunctionPointer<SetConfiguration>(Marshal.ReadIntPtr(table, 19 * IntPtr.Size));
                StreamClose = Marshal.GetDelegateForFunctionPointer<CloseStream>(Marshal.ReadIntPtr(table, 22 * IntPtr.Size));
                StreamShutdown = Marshal.GetDelegateForFunctionPointer<ShutdownStream>(Marshal.ReadIntPtr(table, 24 * IntPtr.Size));
            }
        }
        internal MsQuicNativeConnection AcceptConnection(MsQuicRegistration registration, IntPtr connection)
            => MsQuicNativeConnection.Accept(registration, connection, _connectionFunctions);
    }
    internal sealed partial class MsQuicNativeConnection : SafeHandleZeroOrMinusOneIsInvalid
    {
        private sealed class Signals
        {
            internal readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Closing;
            internal readonly MsQuicApi.ConnectionFunctions.Callback Handler;
            internal readonly IntPtr Pointer;
            internal Func<IntPtr, uint, bool>? AcceptStream;
            internal Action? StopStreams;
            internal MsQuicNativeDatagrams? Datagrams;
            private readonly MsQuicApi.ConnectionFunctions _functions;
            private readonly ConcurrentDictionary<IntPtr, RejectedStream> _rejected = new();
            private sealed class RejectedStream
            {
                private readonly Signals _owner;
                private readonly IntPtr _stream;
                internal readonly MsQuicApi.ConnectionFunctions.Callback Handler;
                private int _closed;
                internal RejectedStream(Signals owner, IntPtr stream)
                { _owner = owner; _stream = stream; Handler = OnEvent; }
                private uint OnEvent(IntPtr stream, IntPtr context, IntPtr eventData)
                {
                    try
                    {
                        if (Marshal.ReadInt32(eventData) == 7) Close();
                        return 0;
                    }
                    catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                    {
                        _owner.Closed.TrySetException(error);
                        return OperatingSystem.IsWindows() ? 0x80004004u : OperatingSystem.IsMacOS() ? 89u : 125u;
                    }
                }
                internal void Close()
                {
                    if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                    _owner._functions.StreamClose(_stream);
                    _owner._rejected.TryRemove(_stream, out _);
                    GC.KeepAlive(Handler);
                }
            }
            private void RejectStream(IntPtr stream)
            {
                var rejected = new RejectedStream(this, stream);
                if (!_rejected.TryAdd(stream, rejected)) throw new IOException("Duplicate native peer stream.");
                _functions.SetHandler(stream, Marshal.GetFunctionPointerForDelegate(rejected.Handler), IntPtr.Zero);
                // ABORT + INLINE is valid here because this is a connection
                // callback. Retain ownership until real SHUTDOWN_COMPLETE;
                // immediate completion followed by close can lose the wire code.
                var status = _functions.StreamShutdown(stream, 0x16, 0x10c);
                if (MsQuicApi.Failed(status)) throw new IOException("MsQuic stream rejection failed with status 0x" + status.ToString("X8"));
                GC.KeepAlive(rejected);
            }
            internal void CloseRejectedAfterConnectionClose()
            {
                // Connection close has finished all callbacks and shut streams down.
                foreach (var rejected in _rejected.Values) rejected.Close();
            }
            internal Signals(MsQuicApi.ConnectionFunctions functions)
            { _functions = functions; Handler = OnEvent; Pointer = Marshal.GetFunctionPointerForDelegate(Handler); }
            private uint OnEvent(IntPtr connection, IntPtr context, IntPtr eventData)
            {
                try
                {
                    var type = Marshal.ReadInt32(eventData);
                    switch (type)
                    {
                        case 0: Datagrams?.OnConnected(_functions.Datagrams.QuerySendEnabled(connection)); Connected.TrySetResult(); break;
                        case 1:
                        case 2: Volatile.Write(ref Closing, 1); Connected.TrySetCanceled(); break;
                        case 3: Volatile.Write(ref Closing, 1); Connected.TrySetCanceled(); StopStreams?.Invoke(); Datagrams?.OnConnectionShutdownComplete(); Closed.TrySetResult(); break;
                        // DATAGRAM_STATE_CHANGED, DATAGRAM_RECEIVED, DATAGRAM_SEND_STATE_CHANGED.
                        case 10:
                        case 11:
                        case 12: Datagrams?.OnConnectionEvent(type, eventData); break;
                        // Streams are not exposed until their own callback/lifetime exists.
                        case 6:
                            var stream = Marshal.ReadIntPtr(eventData, 8);
                            var accept = AcceptStream;
                            if (accept == null || !accept(stream, (uint)Marshal.ReadInt32(eventData, 8 + IntPtr.Size))) RejectStream(stream);
                            break;
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
        internal bool IsClosing => Volatile.Read(ref _shutdown) != 0 || Volatile.Read(ref _signals.Closing) != 0;
        private readonly object _streamSync = new();
        private Channel<MsQuicNativeStream>? _streams;
        private bool _disposing;
        internal void EnableStreamAcceptance()
        {
            lock (_streamSync)
            {
                if (IsClosed || _signals.Connected.Task.IsCompleted) throw new InvalidOperationException("Stream acceptance must be enabled before the handshake completes.");
                if (_streams != null) return;
                var queue = Channel.CreateBounded<MsQuicNativeStream>(128);
                _streams = queue;
                _signals.StopStreams = () => { lock (_streamSync) queue.Writer.TryComplete(); };
                _signals.AcceptStream = (stream, flags) =>
                {
                    // Explicit stream credit controls native concurrency; this
                    // queue additionally bounds unclaimed managed admission.
                    lock (_streamSync)
                    {
                        if (_disposing || IsClosed || queue.Reader.Count >= 128) return false;
                        var owned = MsQuicNativeStream.Accept(this, stream, flags, _functions.Streams);
                        if (!queue.Writer.TryWrite(owned))
                        {
                            // Never replace the callback after ownership transfers.
                            // Disposal and parent release must run outside native callbacks.
                            _ = Task.Run(() =>
                            {
                                try { owned.Dispose(); }
                                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                                { _signals.Closed.TrySetException(error); }
                            });
                        }
                        return true;
                    }
                };
            }
        }
        internal async Task<MsQuicNativeStream> OpenStreamAsync(bool unidirectional, CancellationToken token)
        {
            await Connected.WaitAsync(token).ConfigureAwait(false);
            return await MsQuicNativeStream.OpenAsync(this, unidirectional, _functions.Streams, token).ConfigureAwait(false);
        }
        internal async Task<MsQuicNativeStream> AcceptStreamAsync(CancellationToken token)
            => await (_streams ?? throw new InvalidOperationException("Stream acceptance is not enabled.")).Reader.ReadAsync(token).ConfigureAwait(false);
        protected override void Dispose(bool disposing)
        {
            lock (_streamSync)
            {
                if (_disposing) return;
                _disposing = true;
                _streams?.Writer.TryComplete();
            }
            try
            {
                // Native calls and callback completion never run under _streamSync.
                if (!IsClosed && !IsInvalid)
                {
                    var retained = false;
                    DangerousAddRef(ref retained);
                    try { if (Interlocked.Exchange(ref _shutdown, 1) == 0) _functions.Shutdown(handle, 0, 0x100); }
                    finally { if (retained) DangerousRelease(); }
                }
                if (_streams != null) while (_streams.Reader.TryRead(out var pending)) pending.Dispose();
            }
            finally { base.Dispose(disposing); }
        }
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
            try { _functions.Close(handle); _signals.CloseRejectedAfterConnectionClose(); _signals.Datagrams?.ReleaseAfterConnectionClose(); _signals.Connected.TrySetCanceled(); _signals.Closed.TrySetResult(); GC.KeepAlive(_signals); }
            finally { _registration.DangerousRelease(); }
            return true;
        }
    }
}
#endif
