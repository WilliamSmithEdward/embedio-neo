#if NET10_0_OR_GREATER
using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace EmbedIO.Net.Internal.Http3
{
    internal sealed partial class MsQuicApi
    {
        private readonly StreamFunctions _streamFunctions;
        internal sealed class StreamFunctions
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate uint GetParameter(IntPtr stream, uint parameter, ref uint size, IntPtr buffer);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void CompleteReceive(IntPtr stream, ulong length);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint SendStream(IntPtr stream, IntPtr buffers, uint count, uint flags, IntPtr context);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint OpenStream(IntPtr connection, uint flags, IntPtr callback, IntPtr context, out IntPtr stream);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint StartStream(IntPtr stream, uint flags);
            internal readonly OpenStream Open;
            internal readonly StartStream Start;
            internal readonly SendStream Send;
            private readonly GetParameter _get;
            internal readonly CompleteReceive ReceiveComplete;
            internal readonly ConnectionFunctions.SetCallback SetHandler;
            internal readonly ConnectionFunctions.CloseStream Close;
            internal readonly ConnectionFunctions.ShutdownStream Shutdown;
            internal StreamFunctions(IntPtr table)
            {
                _get = Marshal.GetDelegateForFunctionPointer<GetParameter>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size));
                SetHandler = Marshal.GetDelegateForFunctionPointer<ConnectionFunctions.SetCallback>(Marshal.ReadIntPtr(table, 2 * IntPtr.Size));
                Close = Marshal.GetDelegateForFunctionPointer<ConnectionFunctions.CloseStream>(Marshal.ReadIntPtr(table, 22 * IntPtr.Size));
                Shutdown = Marshal.GetDelegateForFunctionPointer<ConnectionFunctions.ShutdownStream>(Marshal.ReadIntPtr(table, 24 * IntPtr.Size));
                Open = Marshal.GetDelegateForFunctionPointer<OpenStream>(Marshal.ReadIntPtr(table, 21 * IntPtr.Size));
                Start = Marshal.GetDelegateForFunctionPointer<StartStream>(Marshal.ReadIntPtr(table, 23 * IntPtr.Size));
                Send = Marshal.GetDelegateForFunctionPointer<SendStream>(Marshal.ReadIntPtr(table, 25 * IntPtr.Size));
                ReceiveComplete = Marshal.GetDelegateForFunctionPointer<CompleteReceive>(Marshal.ReadIntPtr(table, 26 * IntPtr.Size));
            }
            internal long Id(IntPtr stream)
            {
                var bytes = Marshal.AllocHGlobal(8);
                try
                {
                    uint length = 8;
                    var status = _get(stream, 0x08000000, ref length, bytes);
                    if (Failed(status) || length != 8) throw new IOException("Native stream ID query failed.");
                    return Marshal.ReadInt64(bytes);
                }
                finally { Marshal.FreeHGlobal(bytes); }
            }
        }
    }

    // An owned stream holds its connection and the callback table alive.
    // Receive descriptors are copied, but their payload stays in MsQuic until
    // one matching ReceiveComplete after the entire indication is consumed.
    internal sealed class MsQuicNativeStream : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly struct ReceiveBuffer
        {
            internal readonly IntPtr Pointer;
            internal readonly int Length;
            internal ReceiveBuffer(IntPtr pointer, int length) { Pointer = pointer; Length = length; }
        }
        private sealed class Signals
        {
            internal readonly object Sync = new();
            internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<long>? Started;
            internal readonly TaskCompletionSource? PeerAccepted;
            internal bool StartSucceeded;
            internal TaskCompletionSource Available = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly MsQuicApi.ConnectionFunctions.Callback Handler;
            internal readonly IntPtr Pointer;
            internal ReceiveBuffer[]? Buffers;
            internal long Length;
            internal int Index;
            internal int Offset;
            internal bool Fin;
            internal bool Disposed;
            internal Exception? Error;
            internal TaskCompletionSource<bool>? PendingSend;
            internal bool SendFinished;
            internal bool SendAborted;
            private readonly MsQuicApi.StreamFunctions _functions;
            internal Signals(MsQuicApi.StreamFunctions functions, bool local = false)
            {
                _functions = functions; Handler = OnEvent; Pointer = Marshal.GetFunctionPointerForDelegate(Handler);
                if (local)
                {
                    Started = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PeerAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else StartSucceeded = true;
            }
            private uint OnEvent(IntPtr stream, IntPtr context, IntPtr eventData)
            {
                try
                {
                    lock (Sync)
                    {
                        switch (Marshal.ReadInt32(eventData))
                        {
                            case 0:
                                var status = unchecked((uint)Marshal.ReadInt32(eventData, 8));
                                if (MsQuicApi.Failed(status))
                                {
                                    Started?.TrySetException(new IOException("Native stream start failed with status 0x" + status.ToString("X8")));
                                    Closed.TrySetResult();
                                }
                                else
                                {
                                    StartSucceeded = true;
                                    Started?.TrySetResult(Marshal.ReadInt64(eventData, 16));
                                    if ((Marshal.ReadByte(eventData, 24) & 1) != 0) PeerAccepted?.TrySetResult();
                                }
                                break;
                            case 9: PeerAccepted?.TrySetResult(); break;
                            case 1:
                                var length = checked((long)(ulong)Marshal.ReadInt64(eventData, 16));
                                Fin |= (Marshal.ReadInt32(eventData, 24 + IntPtr.Size + 4) & 2) != 0;
                                if (length == 0) { Available.TrySetResult(); return 0; }
                                if (Buffers != null) throw new IOException("Overlapping native receive indications.");
                                var pointer = Marshal.ReadIntPtr(eventData, 24);
                                var count = checked((int)(uint)Marshal.ReadInt32(eventData, 24 + IntPtr.Size));
                                var buffers = new ReceiveBuffer[count];
                                long total = 0;
                                var pointerOffset = IntPtr.Size;
                                var stride = IntPtr.Size * 2;
                                for (var i = 0; i < count; i++)
                                {
                                    var item = IntPtr.Add(pointer, checked(i * stride));
                                    var size = checked((int)(uint)Marshal.ReadInt32(item));
                                    buffers[i] = new ReceiveBuffer(Marshal.ReadIntPtr(item, pointerOffset), size);
                                    total = checked(total + size);
                                }
                                if (total != length) throw new IOException("Native receive length does not match its buffers.");
                                Buffers = buffers; Length = length; Index = 0; Offset = 0;
                                Available.TrySetResult();
                                return OperatingSystem.IsWindows() ? 0x000703e5u : unchecked((uint)-2);
                            case 2:
                                var pending = PendingSend; PendingSend = null;
                                pending?.TrySetResult(Marshal.ReadByte(eventData, 8) != 0);
                                break;
                            case 5: SendAborted = true; break;
                            case 6: SendFinished = true; break;
                            case 3: Fin = true; Available.TrySetResult(); break;
                            case 4: Error = new IOException("Native peer aborted its stream send direction."); Available.TrySetResult(); break;
                            case 7:
                                PeerAccepted?.TrySetException(new IOException("Native stream closed before the peer accepted it."));
                                if ((!Fin || Buffers != null) && Error == null) Error = new IOException("Native stream closed before pending receive data was consumed.");
                                Available.TrySetResult(); Closed.TrySetResult(); break;
                        }
                        return 0;
                    }
                }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                {
                    lock (Sync) { Error = error; Available.TrySetResult(); }
                    _functions.Shutdown(stream, 0x16, 0x102);
                    return 0;
                }
            }
            internal void StopReads()
            { lock (Sync) { Disposed = true; Available.TrySetResult(); } }
        }
        private readonly MsQuicNativeConnection _connection;
        private readonly MsQuicApi.StreamFunctions _functions;
        private readonly Signals _signals;
        private readonly EmbedIO.Internal.AsyncWriteGate _readGate = new();
        private readonly EmbedIO.Internal.AsyncWriteGate _writeGate = new();
        private int _disposeStarted;
        private bool _finQueued;
        private long _id;
        private readonly bool _local;
        internal long Id => _id;
        internal bool Unidirectional { get; }
        private MsQuicNativeStream(MsQuicNativeConnection connection, MsQuicApi.StreamFunctions functions, Signals signals, long id, uint flags, bool local = false) : base(true)
        { _connection = connection; _functions = functions; _signals = signals; _id = id; _local = local; Unidirectional = (flags & 1) != 0; }
        internal static MsQuicNativeStream Accept(MsQuicNativeConnection connection, IntPtr handle, uint flags, MsQuicApi.StreamFunctions functions)
        {
            if (handle == IntPtr.Zero) throw new ArgumentException("Missing accepted stream.", nameof(handle));
            var signals = new Signals(functions);
            var result = new MsQuicNativeStream(connection, functions, signals, functions.Id(handle), flags);
            var retained = false;
            connection.DangerousAddRef(ref retained);
            try
            {
                functions.SetHandler(handle, signals.Pointer, IntPtr.Zero);
                result.SetHandle(handle);
                retained = false;
                return result;
            }
            finally { if (retained) connection.DangerousRelease(); }
        }
        internal static async Task<MsQuicNativeStream> OpenAsync(MsQuicNativeConnection connection, bool unidirectional, MsQuicApi.StreamFunctions functions, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var signals = new Signals(functions, true);
            if (unidirectional) signals.Fin = true; // Locally opened uni streams have no receive direction.
            var result = new MsQuicNativeStream(connection, functions, signals, -1, unidirectional ? 1u : 0u, true);
            var retained = false;
            var stream = IntPtr.Zero;
            connection.DangerousAddRef(ref retained);
            try
            {
                var status = functions.Open(connection.DangerousGetHandle(), unidirectional ? 1u : 0u, signals.Pointer, IntPtr.Zero, out stream);
                if (MsQuicApi.Failed(status)) throw new IOException("Native stream allocation failed with status 0x" + status.ToString("X8"));
                if (stream == IntPtr.Zero) throw new IOException("Native stream allocation returned no handle.");
                result.SetHandle(stream); stream = IntPtr.Zero; retained = false;
                token.ThrowIfCancellationRequested();
                // IMMEDIATE + INDICATE_PEER_ACCEPT assigns an ID, then observes
                // actual peer credit rather than exposing a merely local queued stream.
                status = functions.Start(result.handle, 9);
                if (MsQuicApi.Failed(status))
                {
                    var failure = new IOException("Native stream start failed with status 0x" + status.ToString("X8"));
                    signals.Started?.TrySetException(failure);
                    _ = signals.Started?.Task.Exception;
                    throw failure;
                }
                result._id = await (signals.Started ?? throw new IOException("Missing stream start completion.")).Task.WaitAsync(token).ConfigureAwait(false);
                await (signals.PeerAccepted ?? throw new IOException("Missing peer credit completion.")).Task.WaitAsync(token).ConfigureAwait(false);
                return result;
            }
            catch { result.Dispose(); _ = signals.Started?.Task.Exception; _ = signals.PeerAccepted?.Task.Exception; throw; }
            finally
            {
                if (stream != IntPtr.Zero) functions.Close(stream); // Allocation never started.
                if (retained) connection.DangerousRelease();
            }
        }
        internal async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (IsClosed) throw new ObjectDisposedException(nameof(MsQuicNativeStream));
            if (_local && Unidirectional) throw new NotSupportedException("A local unidirectional stream is send-only.");
            if (destination.IsEmpty) return 0;
            using var scope = await _readGate.EnterAsync(token).ConfigureAwait(false);
            var retained = false;
            try
            {
                DangerousAddRef(ref retained);
                while (true)
                {
                    Task wait;
                    ulong completed = 0;
                    var copied = 0;
                    lock (_signals.Sync)
                    {
                        if (_signals.Disposed) throw new ObjectDisposedException(nameof(MsQuicNativeStream));
                        if (_signals.Error != null) throw _signals.Error;
                        var buffers = _signals.Buffers;
                        if (buffers != null)
                        {
                            while (_signals.Index < buffers.Length && copied < destination.Length)
                            {
                                var item = buffers[_signals.Index];
                                var amount = Math.Min(item.Length - _signals.Offset, destination.Length - copied);
                                var target = destination.Slice(copied, amount);
                                if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)target, out var array) && array.Array != null)
                                    Marshal.Copy(IntPtr.Add(item.Pointer, _signals.Offset), array.Array, array.Offset, amount);
                                else
                                {
                                    var scratch = ArrayPool<byte>.Shared.Rent(amount);
                                    try { Marshal.Copy(IntPtr.Add(item.Pointer, _signals.Offset), scratch, 0, amount); scratch.AsSpan(0, amount).CopyTo(target.Span); }
                                    finally { ArrayPool<byte>.Shared.Return(scratch, true); }
                                }
                                copied += amount; _signals.Offset += amount;
                                if (_signals.Offset == item.Length) { _signals.Index++; _signals.Offset = 0; }
                            }
                            if (_signals.Index == buffers.Length)
                            {
                                completed = (ulong)_signals.Length; _signals.Buffers = null;
                                _signals.Available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            }
                        }
                        if (copied == 0 && _signals.Fin) return 0;
                        wait = _signals.Available.Task;
                    }
                    if (completed != 0) _functions.ReceiveComplete(handle, completed);
                    if (copied != 0) return copied;
                    await wait.WaitAsync(token).ConfigureAwait(false);
                }
            }
            finally { if (retained) DangerousRelease(); }
        }
        internal async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, bool completeWrites, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Unidirectional && !_local) throw new InvalidOperationException("A peer-initiated unidirectional stream is receive-only.");
            using var scope = await _writeGate.EnterAsync(token).ConfigureAwait(false);
            var retained = false;
            var pin = default(GCHandle);
            byte[]? pooled = null;
            var descriptor = IntPtr.Zero;
            TaskCompletionSource<bool>? pending = null;
            try
            {
                DangerousAddRef(ref retained);
                lock (_signals.Sync)
                {
                    if (_signals.Disposed) throw new ObjectDisposedException(nameof(MsQuicNativeStream));
                    if (_finQueued || _signals.SendFinished || _signals.SendAborted) throw new IOException("Native stream send direction is closed.");
                    pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _signals.PendingSend = pending;
                }
                ArraySegment<byte> bytes;
                if (!MemoryMarshal.TryGetArray(payload, out bytes) || bytes.Array == null)
                {
                    pooled = ArrayPool<byte>.Shared.Rent(payload.Length);
                    payload.Span.CopyTo(pooled); bytes = new ArraySegment<byte>(pooled, 0, payload.Length);
                }
                pin = GCHandle.Alloc(bytes.Array, GCHandleType.Pinned);
                descriptor = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteInt32(descriptor, bytes.Count);
                Marshal.WriteIntPtr(descriptor, IntPtr.Size, IntPtr.Add(pin.AddrOfPinnedObject(), bytes.Offset));
                token.ThrowIfCancellationRequested();
                var status = _functions.Send(handle, descriptor, 1, completeWrites ? 4u : 0u, IntPtr.Zero);
                if (MsQuicApi.Failed(status)) throw new IOException("MsQuic send failed with status 0x" + status.ToString("X8"));
                _finQueued = completeWrites;
                bool cancelled;
                try { cancelled = await pending.Task.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // A committed send retains its pin/descriptor until SEND_COMPLETE,
                    // even when the caller cancels while waiting for native ownership.
                    _functions.Shutdown(handle, 2, 0x10c);
                    await pending.Task.ConfigureAwait(false);
                    throw;
                }
                if (cancelled) throw new IOException("Native stream send was cancelled by shutdown.");
            }
            finally
            {
                lock (_signals.Sync)
                {
                    if (ReferenceEquals(_signals.PendingSend, pending)) _signals.PendingSend = null;
                }
                if (descriptor != IntPtr.Zero) Marshal.FreeHGlobal(descriptor);
                if (pin.IsAllocated) pin.Free();
                if (pooled != null) ArrayPool<byte>.Shared.Return(pooled, true);
                if (retained) DangerousRelease();
            }
        }
        private uint AbortFlags => _signals.PeerAccepted != null && !_signals.PeerAccepted.Task.IsCompletedSuccessfully ? 0x0eu : 6u;
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            var retained = false;
            try
            {
                if (!IsClosed && !IsInvalid) DangerousAddRef(ref retained);
                _signals.StopReads(); _readGate.Dispose(); _writeGate.Dispose();
                if (retained && _signals.StartSucceeded) _functions.Shutdown(handle, AbortFlags, 0x10c);
            }
            finally
            {
                try { base.Dispose(disposing); }
                finally { if (retained) DangerousRelease(); }
            }
        }
        protected override bool ReleaseHandle()
        {
            try
            {
                if (_signals.StartSucceeded && !_signals.Closed.Task.IsCompleted)
                {
                    _functions.Shutdown(handle, AbortFlags, 0x10c);
                    _signals.Closed.Task.GetAwaiter().GetResult();
                }
                _functions.Close(handle);
                GC.KeepAlive(_signals);
            }
            finally { _connection.DangerousRelease(); }
            return true;
        }
    }
}
#endif
