using System;
using System.IO;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2HeaderBlock
    {
        internal Http2HeaderBlock(int streamId, bool endStream, HpackField[] fields, uint streamError)
        { StreamId = streamId; EndStream = endStream; Fields = fields; StreamError = streamError; }
        public int StreamId { get; }
        public bool EndStream { get; }
        public HpackField[] Fields { get; }
        public uint StreamError { get; }
    }

    // Every incoming frame passes this guard before control/stream dispatch.
    // HPACK state is shared across streams, including streams later rejected.
    internal sealed class Http2HeaderBlocks : IDisposable
    {
        private readonly HpackDecoder _decoder = new();
        private readonly int _maximumBytes;
        private readonly int _maximumFragments;
        private MemoryStream? _buffer;
        private int _streamId;
        private int _fragments;
        private bool _endStream;
        private uint _streamError;
        private bool _failed;
        private bool _disposed;

        internal Http2HeaderBlocks(int maximumBytes = 65536, int maximumFragments = 1024)
        {
            if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (maximumFragments < 1) throw new ArgumentOutOfRangeException(nameof(maximumFragments));
            _maximumBytes = maximumBytes;
            _maximumFragments = maximumFragments;
        }

        internal Http2HeaderBlock? Process(Http2Frame frame)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Http2HeaderBlocks));
            if (_failed) throw new Http2ProtocolException(1, "Header sequence is no longer usable.");
            try
            {
                if (_streamId != 0)
                {
                    if (frame.Type != 9 || frame.StreamId != _streamId)
                        throw new Http2ProtocolException(1, "Interrupted HTTP/2 header block.");
                }
                else
                {
                    if (frame.Type == 9) throw new Http2ProtocolException(1, "Unexpected CONTINUATION.");
                    if (frame.Type != 1) return null;
                }
                frame.ValidateShape();
                var offset = 0;
                var count = frame.Payload.Length;
                if (frame.Type == 1)
                {
                    _streamId = frame.StreamId;
                    _endStream = (frame.Flags & 1) != 0;
                    _streamError = 0;
                    if ((frame.Flags & 8) != 0) { offset++; count -= 1 + frame.Payload[0]; }
                    if ((frame.Flags & 32) != 0)
                    {
                        var dependency = Http2PeerSettings.ReadUInt32(frame.Payload, offset) & 0x7fffffff;
                        if (dependency == frame.StreamId) _streamError = 1;
                        offset += 5;
                        count -= 5;
                    }
                }
                if (++_fragments > _maximumFragments || count > _maximumBytes - (_buffer?.Length ?? 0))
                    throw new Http2ProtocolException(11, "HTTP/2 header block exceeds resource limits.");
                if ((frame.Flags & 4) == 0)
                {
                    _buffer ??= new MemoryStream(Math.Min(_maximumBytes, Math.Max(count, 256)));
                    _buffer.Write(frame.Payload, offset, count);
                    return null;
                }
                byte[] encoded;
                if (_buffer != null)
                {
                    _buffer.Write(frame.Payload, offset, count);
                    encoded = _buffer.ToArray();
                }
                else if (offset == 0 && count == frame.Payload.Length) encoded = frame.Payload;
                else
                {
                    encoded = new byte[count];
                    Buffer.BlockCopy(frame.Payload, offset, encoded, 0, count);
                }
                HpackField[] fields;
                try { fields = _decoder.Decode(encoded); }
                catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException)
                { throw new Http2ProtocolException(9, "Invalid HPACK header block."); }
                var block = new Http2HeaderBlock(_streamId, _endStream, fields, _streamError);
                Reset();
                return block;
            }
            catch { _failed = true; Reset(); throw; }
        }

        internal void CompleteInput()
        {
            if (_streamId != 0)
            {
                _failed = true;
                Reset();
                throw new Http2ProtocolException(1, "Incomplete HTTP/2 header block.");
            }
        }

        private void Reset()
        {
            _buffer?.Dispose();
            _buffer = null;
            _streamId = 0;
            _fragments = 0;
        }

        public void Dispose() { _disposed = true; Reset(); }
    }
}
