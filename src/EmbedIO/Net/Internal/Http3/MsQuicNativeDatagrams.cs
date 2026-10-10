#if NET10_0_OR_GREATER
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    internal sealed partial class MsQuicApi
    {
        // Public MsQuic API v2 slots: SetParam (3), GetParam (4) and DatagramSend
        // (28), as laid out in the published v2.6.2 QUIC_API_TABLE.
        internal sealed class DatagramFunctions
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint SetParameter(IntPtr handle, uint parameter, uint size, IntPtr buffer);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate uint GetParameter(IntPtr handle, uint parameter, ref uint size, IntPtr buffer);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint SendDatagram(IntPtr connection, IntPtr buffers, uint count, uint flags, IntPtr context);
            private const uint SendEnabledParameter = 0x0500000E;
            internal readonly SetParameter Set;
            private readonly GetParameter _get;
            internal readonly SendDatagram Send;
            internal DatagramFunctions(IntPtr table)
            {
                Set = Marshal.GetDelegateForFunctionPointer<SetParameter>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
                _get = Marshal.GetDelegateForFunctionPointer<GetParameter>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size));
                Send = Marshal.GetDelegateForFunctionPointer<SendDatagram>(Marshal.ReadIntPtr(table, 28 * IntPtr.Size));
            }

            // QUIC_PARAM_CONN_DATAGRAM_SEND_ENABLED; null if the query fails.
            // Called on the connection callback thread, where MsQuic runs it inline.
            internal bool? QuerySendEnabled(IntPtr connection)
            {
                var value = Marshal.AllocHGlobal(1);
                try
                {
                    uint size = 1;
                    var status = _get(connection, SendEnabledParameter, ref size, value);
                    return MsQuicApi.Failed(status) || size != 1 ? null : Marshal.ReadByte(value) != 0;
                }
                finally { Marshal.FreeHGlobal(value); }
            }
        }
    }

    internal sealed partial class MsQuicNativeConnection
    {
        internal const int DefaultDatagramReceiveCapacity = 128;
        internal const int DefaultDatagramReceiveByteLimit = 256 * 1024;
        internal const int DefaultDatagramSendCapacity = 128;
        private const uint DatagramReceiveEnabledParameter = 0x0500000D;

        internal MsQuicNativeDatagrams EnableDatagrams()
            => EnableDatagrams(DefaultDatagramReceiveCapacity, DefaultDatagramReceiveByteLimit, DefaultDatagramSendCapacity);

        // Advertises max_datagram_frame_size for this connection only. MsQuic
        // accepts the parameter only before ConnectionSetConfiguration starts
        // the handshake, so a late call fails without changing the connection.
        internal MsQuicNativeDatagrams EnableDatagrams(int receiveCapacity, int receiveByteLimit, int sendCapacity)
        {
            // The connection event union offset below is only established for
            // 64-bit layouts; 32-bit ABIs differ in 64-bit member alignment.
            if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Native QUIC datagrams require a 64-bit process.");
            var datagrams = new MsQuicNativeDatagrams(this, _functions.Datagrams.Send, receiveCapacity, receiveByteLimit, sendCapacity);
            if (Interlocked.CompareExchange(ref _signals.Datagrams, datagrams, null) != null)
                throw new InvalidOperationException("Datagrams are already enabled for this connection.");
            var retained = false;
            var value = IntPtr.Zero;
            try
            {
                DangerousAddRef(ref retained);
                value = Marshal.AllocHGlobal(1);
                Marshal.WriteByte(value, 1);
                var status = _functions.Datagrams.Set(handle, DatagramReceiveEnabledParameter, 1, value);
                if (MsQuicApi.Failed(status))
                    throw new InvalidOperationException("Datagrams must be enabled before the connection is configured (MsQuic status 0x" + status.ToString("X8") + ").");
                return datagrams;
            }
            catch
            {
                // No send can have been submitted through an object never returned.
                Interlocked.CompareExchange(ref _signals.Datagrams, null, datagrams);
                datagrams.Dispose();
                throw;
            }
            finally
            {
                if (value != IntPtr.Zero) Marshal.FreeHGlobal(value);
                if (retained) DangerousRelease();
            }
        }
    }

    internal enum MsQuicDatagramSendStatus
    {
        Queued,
        // The peer did not advertise datagram support, or negotiation is not yet known.
        Unavailable,
        // Larger than the current negotiated maximum.
        TooLarge,
        // Too many submitted datagrams have not reached a final native state.
        QueueFull,
        // The datagram owner or its connection has finished.
        Closed,
    }

    internal enum MsQuicDatagramOutcome
    {
        Acknowledged,
        // LOST_DISCARDED: never acknowledged, including datagrams the peer may
        // have received but not acknowledged before the connection shut down.
        Lost,
        Canceled,
    }

    // An owned copy of one received datagram. Disposal clears and returns its
    // pooled storage; Payload must not be used afterwards.
    internal sealed class MsQuicReceivedDatagram : IDisposable
    {
        private byte[]? _buffer;
        internal MsQuicReceivedDatagram(byte[] buffer, int length) { _buffer = buffer; Length = length; }
        internal int Length { get; }
        internal ReadOnlyMemory<byte> Payload
            => new(Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(MsQuicReceivedDatagram)), 0, Length);
        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer, true);
        }
    }

    // Unreliable datagram delivery for one native connection (RFC 9221).
    //
    // Receive: MsQuic lends the payload only for the duration of the
    // DATAGRAM_RECEIVED callback, so it is copied there into pooled storage.
    // Queued datagrams are bounded by count and bytes; arrivals beyond either
    // bound are dropped and counted, as unreliable delivery permits.
    //
    // Send: each submission owns one native allocation holding its QUIC_BUFFER
    // descriptor and payload, because MsQuic retains both pointers. Ownership
    // returns only at a final DATAGRAM_SEND_STATE_CHANGED (acknowledged, lost
    // and discarded, or canceled), or if a synchronous submission failure means
    // MsQuic never took it. Payload bytes are cleared before release.
    internal sealed class MsQuicNativeDatagrams : IDisposable
    {
        // QUIC_CONNECTION_EVENT on 64-bit layouts: Type, then the union at 8.
        private const int Union = 8;
        private const int DescriptorSize = 16;
        private readonly object _sync = new();
        private readonly SafeHandle _connection;
        private readonly MsQuicApi.DatagramFunctions.SendDatagram _send;
        private readonly int _receiveCapacity;
        private readonly int _receiveByteLimit;
        private readonly int _sendCapacity;
        private readonly Channel<MsQuicReceivedDatagram> _received = Channel.CreateUnbounded<MsQuicReceivedDatagram>(new UnboundedChannelOptions { SingleWriter = true });
        private readonly HashSet<PendingSend> _pending = new();
        private readonly TaskCompletionSource<bool> _negotiated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _queuedCount;
        private long _queuedBytes;
        private bool _sendEnabled;
        private int _maxSendLength;
        private bool _limitIndicated;
        private bool _connectionEnded;
        private bool _disposed;
        private long _receiveDropped;
        private long _abandonedSends;

        private sealed class PendingSend
        {
            internal IntPtr Memory;
            internal int Length;
            internal GCHandle Handle;
            internal readonly TaskCompletionSource<MsQuicDatagramOutcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal MsQuicNativeDatagrams(SafeHandle connection, MsQuicApi.DatagramFunctions.SendDatagram send, int receiveCapacity, int receiveByteLimit, int sendCapacity)
        {
            if (receiveCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(receiveCapacity));
            if (receiveByteLimit <= 0) throw new ArgumentOutOfRangeException(nameof(receiveByteLimit));
            if (sendCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(sendCapacity));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _receiveCapacity = receiveCapacity;
            _receiveByteLimit = receiveByteLimit;
            _sendCapacity = sendCapacity;
        }

        // Completes with whether the peer advertised max_datagram_frame_size,
        // or false if the connection ends before the handshake completes.
        internal Task<bool> SendNegotiated => _negotiated.Task;
        internal bool SendEnabled { get { lock (_sync) return _sendEnabled; } }
        // The last limit MsQuic indicated. A server processes the peer's transport
        // parameters before the connection has an owner, so MsQuic may indicate
        // nothing until a path MTU change; meanwhile the protocol ceiling 65535
        // applies here and MsQuic enforces its own limit synchronously.
        internal int MaxSendLength { get { lock (_sync) return _sendEnabled ? _maxSendLength : 0; } }
        internal bool MaxSendLengthIndicated { get { lock (_sync) return _limitIndicated; } }
        internal int PendingSendCount { get { lock (_sync) return _pending.Count; } }
        internal int QueuedReceiveCount => Volatile.Read(ref _queuedCount);
        internal long ReceiveDropped => Interlocked.Read(ref _receiveDropped);
        // Final-state indications MsQuic never delivered before connection close.
        internal long AbandonedSends => Interlocked.Read(ref _abandonedSends);

        internal MsQuicDatagramSendStatus TrySend(ReadOnlyMemory<byte> payload, out Task<MsQuicDatagramOutcome>? completion)
        {
            completion = null;
            PendingSend? record = null;
            var submitted = false;
            var retained = false;
            try
            {
                // ConnectionClose inspects _pending and frees leftover records.
                // Hold the handle before publishing a record, including while
                // user-owned memory is copied and submission can still fail.
                try { _connection.DangerousAddRef(ref retained); }
                catch (ObjectDisposedException) { return MsQuicDatagramSendStatus.Closed; }
                lock (_sync)
                {
                    if (_disposed || _connectionEnded) return MsQuicDatagramSendStatus.Closed;
                    // Submit only once the peer's support is known.
                    if (!_sendEnabled) return MsQuicDatagramSendStatus.Unavailable;
                    if (payload.Length > _maxSendLength) return MsQuicDatagramSendStatus.TooLarge;
                    if (_pending.Count >= _sendCapacity) return MsQuicDatagramSendStatus.QueueFull;
                    record = new PendingSend { Length = payload.Length };
                    _pending.Add(record); // Reserves the slot before allocation.
                }
                record.Memory = Marshal.AllocHGlobal(DescriptorSize + payload.Length);
                var data = IntPtr.Add(record.Memory, DescriptorSize);
                Marshal.WriteInt32(record.Memory, payload.Length);
                Marshal.WriteIntPtr(record.Memory, IntPtr.Size, data);
                CopyToNative(payload, data);
                // A non-null context is required: MsQuic suppresses discard
                // indications for frames whose context is null.
                record.Handle = GCHandle.Alloc(record);
                var status = _send(_connection.DangerousGetHandle(), record.Memory, 1, 0, GCHandle.ToIntPtr(record.Handle));
                if (!MsQuicApi.Failed(status))
                {
                    // MsQuic owns the allocation now; a final callback may already
                    // have released it, so record fields are not touched again.
                    submitted = true;
                    completion = record.Completion.Task;
                    return MsQuicDatagramSendStatus.Queued;
                }
                if (status == InvalidParameter) return MsQuicDatagramSendStatus.TooLarge;
                if (status == InvalidState)
                {
                    lock (_sync) return _disposed || _connectionEnded ? MsQuicDatagramSendStatus.Closed : MsQuicDatagramSendStatus.Unavailable;
                }
                throw new IOException("MsQuic datagram send failed with status 0x" + status.ToString("X8"));
            }
            finally
            {
                // Synchronous failure: MsQuic freed its request and kept no pointer.
                // Remove it before releasing the lease: ConnectionClose must
                // never encounter a partially initialized or refused record.
                try { if (!submitted && record != null) Release(record, null); }
                finally { if (retained) _connection.DangerousRelease(); }
            }
        }

        // Returns null once the connection has ended and every queued datagram
        // has been read. Cancellation leaves queued datagrams in place.
        internal async ValueTask<MsQuicReceivedDatagram?> ReceiveAsync(CancellationToken token)
        {
            if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(MsQuicNativeDatagrams));
            var reader = _received.Reader;
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                if (reader.TryRead(out var datagram))
                {
                    Interlocked.Decrement(ref _queuedCount);
                    Interlocked.Add(ref _queuedBytes, -datagram.Length);
                    return datagram;
                }
            }
            if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(MsQuicNativeDatagrams));
            return null;
        }

        // Runs on the connection callback thread. MsQuic serializes callbacks for
        // one connection, so this is the queue's only writer.
        internal void OnConnectionEvent(int type, IntPtr eventData)
        {
            switch (type)
            {
                case 10: OnStateChanged(Marshal.ReadByte(eventData, Union) != 0, (ushort)Marshal.ReadInt16(eventData, Union + 2)); break;
                case 11: OnReceived(eventData); break;
                case 12: OnSendStateChanged(eventData); break;
            }
        }

        private void OnStateChanged(bool enabled, int maxLength)
        {
            lock (_sync)
            {
                _sendEnabled = enabled && !_connectionEnded;
                _maxSendLength = enabled ? maxLength : 0;
                _limitIndicated = true;
            }
            _negotiated.TrySetResult(enabled);
        }

        // CONNECTED: the peer's transport parameters are final. sendEnabled is
        // QUIC_PARAM_CONN_DATAGRAM_SEND_ENABLED, or null if it could not be read.
        internal void OnConnected(bool? sendEnabled)
        {
            bool enabled;
            lock (_sync)
            {
                if (!_limitIndicated && !_connectionEnded)
                {
                    _sendEnabled = sendEnabled == true;
                    _maxSendLength = _sendEnabled ? ushort.MaxValue : 0;
                }
                enabled = _sendEnabled;
            }
            _negotiated.TrySetResult(enabled);
        }

        private void OnReceived(IntPtr eventData)
        {
            byte[]? buffer = null;
            try
            {
                if (Volatile.Read(ref _disposed)) { Interlocked.Increment(ref _receiveDropped); return; }
                var descriptor = Marshal.ReadIntPtr(eventData, Union);
                var length = (int)(uint)Marshal.ReadInt32(descriptor);
                if (length < 0 || Volatile.Read(ref _queuedCount) >= _receiveCapacity
                    || Interlocked.Read(ref _queuedBytes) + length > _receiveByteLimit)
                {
                    Interlocked.Increment(ref _receiveDropped);
                    return;
                }
                buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
                // The borrowed native payload is valid only during this callback.
                if (length != 0) Marshal.Copy(Marshal.ReadIntPtr(descriptor, IntPtr.Size), buffer, 0, length);
                var datagram = new MsQuicReceivedDatagram(buffer, length);
                buffer = null;
                Interlocked.Increment(ref _queuedCount);
                Interlocked.Add(ref _queuedBytes, length);
                if (!_received.Writer.TryWrite(datagram))
                {
                    Interlocked.Decrement(ref _queuedCount);
                    Interlocked.Add(ref _queuedBytes, -length);
                    Interlocked.Increment(ref _receiveDropped);
                    datagram.Dispose();
                }
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error) || error is OutOfMemoryException)
            {
                Interlocked.Increment(ref _receiveDropped);
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer, true);
            }
        }

        private void OnSendStateChanged(IntPtr eventData)
        {
            var context = Marshal.ReadIntPtr(eventData, Union);
            if (context == IntPtr.Zero) return;
            if (GCHandle.FromIntPtr(context).Target is not PendingSend record) return;
            var state = Marshal.ReadInt32(eventData, Union + IntPtr.Size);
            // UNKNOWN, SENT and LOST_SUSPECT are still tracked by MsQuic.
            if (state < 3) return;
            // QUIC_DATAGRAM_SEND_STATE_IS_FINAL: every value from LOST_DISCARDED.
            // Clearing the in/out context stops any further indication for it.
            Marshal.WriteIntPtr(eventData, Union, IntPtr.Zero);
            Release(record, state switch
            {
                4 or 5 => MsQuicDatagramOutcome.Acknowledged,
                6 => MsQuicDatagramOutcome.Canceled,
                _ => MsQuicDatagramOutcome.Lost,
            });
        }

        // Exactly-once release of a send record's native memory and handle.
        private void Release(PendingSend record, MsQuicDatagramOutcome? outcome)
        {
            lock (_sync)
            {
                if (!_pending.Remove(record)) return;
            }
            if (record.Memory != IntPtr.Zero)
            {
                ClearNative(record.Memory, DescriptorSize + record.Length);
                Marshal.FreeHGlobal(record.Memory);
                record.Memory = IntPtr.Zero;
            }
            if (record.Handle.IsAllocated) record.Handle.Free();
            if (outcome.HasValue) record.Completion.TrySetResult(outcome.Value);
        }

        // SHUTDOWN_COMPLETE: no further connection callbacks follow. Datagrams
        // already received stay readable; no new datagram can be sent.
        internal void OnConnectionShutdownComplete()
        {
            lock (_sync) { _connectionEnded = true; _sendEnabled = false; _maxSendLength = 0; }
            _negotiated.TrySetResult(false);
            _received.Writer.TryComplete();
        }

        // Runs after ConnectionClose returns, when MsQuic retains no pointer from
        // this connection. MsQuic finalizes every send before SHUTDOWN_COMPLETE,
        // so anything left here is counted rather than silently leaked.
        internal void ReleaseAfterConnectionClose()
        {
            OnConnectionShutdownComplete();
            Dispose();
            PendingSend[] remaining;
            lock (_sync) { remaining = new PendingSend[_pending.Count]; _pending.CopyTo(remaining); }
            foreach (var record in remaining)
            {
                Interlocked.Increment(ref _abandonedSends);
                Release(record, MsQuicDatagramOutcome.Canceled);
            }
        }

        // Stops delivery and discards unread datagrams. Submitted sends stay
        // owned by MsQuic until their final state or connection close.
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _received.Writer.TryComplete();
            while (_received.Reader.TryRead(out var unread))
            {
                Interlocked.Decrement(ref _queuedCount);
                Interlocked.Add(ref _queuedBytes, -unread.Length);
                unread.Dispose();
            }
        }

        private static void CopyToNative(ReadOnlyMemory<byte> payload, IntPtr destination)
        {
            if (payload.IsEmpty) return;
            if (MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array != null)
            {
                Marshal.Copy(segment.Array, segment.Offset, destination, segment.Count);
                return;
            }
            var scratch = ArrayPool<byte>.Shared.Rent(payload.Length);
            try { payload.Span.CopyTo(scratch); Marshal.Copy(scratch, 0, destination, payload.Length); }
            finally { ArrayPool<byte>.Shared.Return(scratch, true); }
        }

        private static readonly byte[] Zeros = new byte[4096];
        private static void ClearNative(IntPtr memory, int length)
        {
            for (var offset = 0; offset < length; offset += Zeros.Length)
                Marshal.Copy(Zeros, 0, IntPtr.Add(memory, offset), Math.Min(Zeros.Length, length - offset));
        }

        private static uint InvalidParameter => OperatingSystem.IsWindows() ? 0x80070057u : 22u;
        private static uint InvalidState => OperatingSystem.IsWindows() ? 0x8007139Fu : 1u;
    }
}
#endif
