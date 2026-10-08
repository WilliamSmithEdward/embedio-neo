using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    // Owns its read-ahead until the terminal trailer line. Only then may the
    // connection adopt BufferedRemainder for the next request.
    internal sealed class ChunkedRequestStream : RequestStream
    {
        private enum Phase { Size, Data, DataCr, DataLf, Trailers, Complete, Failed }
        private readonly EmbedIO.Internal.BorrowedResource<Stream> _source;
        private byte[] _input;
        private int _offset;
        private int _available;
        private bool _ownsInput;
        private long _remaining;
        private Phase _phase;
        private readonly StringBuilder _line = new();
        private bool _lineCr;
        private int _trailerBytes;

        internal ChunkedRequestStream(Stream source, byte[] buffer, int offset, int length)
            : base(source, Array.Empty<byte>(), 0, 0, 0)
        {
            _source = new EmbedIO.Internal.BorrowedResource<Stream>(source);
            _input = buffer;
            _offset = offset;
            _available = length;
        }

        internal override bool IsBodyConsumed => _phase == Phase.Complete;
        internal override ArraySegment<byte> BufferedRemainder => IsBodyConsumed
            ? new ArraySegment<byte>(_input, _offset, _available) : default;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateDestination(buffer, offset, count);
            return ReadCoreAsync(buffer, offset, count, false, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateDestination(buffer, offset, count);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);
            return ReadCoreAsync(buffer, offset, count, true, cancellationToken);
        }

#if NET10_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var array))
                return new ValueTask<int>(ReadAsync(array.Array ?? throw new InvalidOperationException("Missing array-backed read buffer."), array.Offset, array.Count, cancellationToken));
            return ReadMemoryAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> ReadMemoryAsync(Memory<byte> destination, CancellationToken token)
        {
            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
            if (destination.Length == 0) return 0;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(destination.Length, 8192));
            try
            {
                var read = await ReadAsync(buffer, 0, Math.Min(buffer.Length, destination.Length), token).ConfigureAwait(false);
                buffer.AsMemory(0, read).CopyTo(destination);
                return read;
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, true); }
        }
#endif

        private async Task<int> ReadCoreAsync(byte[] destination, int offset, int count, bool asynchronous, CancellationToken token)
        {
            if (_phase == Phase.Failed) throw new InvalidDataException("Invalid chunked request body.");
            if (count == 0 || IsBodyConsumed) return 0;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (_available == 0)
                    {
                        if (!_ownsInput) { _input = new byte[8192]; _ownsInput = true; }
                        _offset = 0;
                        _available = asynchronous
                            ? await _source.Value.ReadAsync(_input, 0, _input.Length, token).ConfigureAwait(false)
                            : _source.Value.Read(_input, 0, _input.Length);
                        if (_available == 0) throw new EndOfStreamException("Incomplete chunked request body.");
                    }

                    if (_phase == Phase.Data)
                    {
                        var size = (int)Math.Min(Math.Min(count, _available), _remaining);
                        Buffer.BlockCopy(_input, _offset, destination, offset, size);
                        _offset += size;
                        _available -= size;
                        _remaining -= size;
                        if (_remaining == 0) _phase = Phase.DataCr;
                        return size;
                    }

                    var next = _input[_offset++];
                    _available--;
                    if (_phase == Phase.DataCr)
                    {
                        if (next != 13) throw new InvalidDataException("Missing chunk CRLF.");
                        _phase = Phase.DataLf;
                    }
                    else if (_phase == Phase.DataLf)
                    {
                        if (next != 10) throw new InvalidDataException("Missing chunk CRLF.");
                        _phase = Phase.Size;
                    }
                    else
                    {
                        if (_phase == Phase.Trailers && ++_trailerBytes > 32768)
                            throw new InvalidDataException("Request trailers exceed 32768 bytes.");
                        if (_lineCr)
                        {
                            if (next != 10) throw new InvalidDataException("Invalid chunk line ending.");
                            _lineCr = false;
                            var line = _line.ToString();
                            _line.Clear();
                            if (_phase == Phase.Size)
                            {
                                _remaining = ParseSize(line);
                                _phase = _remaining == 0 ? Phase.Trailers : Phase.Data;
                            }
                            else if (line.Length == 0)
                            {
                                _phase = Phase.Complete;
                                return 0;
                            }
                            else ValidateTrailer(line);
                        }
                        else if (next == 13) _lineCr = true;
                        else
                        {
                            if (next == 10 || next == 0 || (next < 32 && next != 9) || next == 127)
                                throw new InvalidDataException("Invalid chunk metadata.");
                            if (_line.Length >= 8192) throw new InvalidDataException("Chunk metadata exceeds 8192 bytes.");
                            _line.Append((char)next);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { _phase = Phase.Failed; throw; }
        }

        private static long ParseSize(string line)
        {
            var index = 0;
            long size = 0;
            while (index < line.Length)
            {
                var c = line[index];
                var digit = c >= '0' && c <= '9' ? c - '0'
                    : c >= 'a' && c <= 'f' ? c - 'a' + 10
                    : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0) break;
                if (size > (long.MaxValue - digit) / 16) throw new InvalidDataException("Chunk size overflow.");
                size = size * 16 + digit;
                index++;
            }
            if (index == 0) throw new InvalidDataException("Missing chunk size.");
            // Extensions are optional tokens or quoted strings; do not accept arbitrary suffixes.
            while (index < line.Length)
            {
                SkipWhitespace(line, ref index);
                if (index == line.Length || line[index++] != ';') throw new InvalidDataException("Invalid chunk extension.");
                SkipWhitespace(line, ref index);
                ReadToken(line, ref index);
                SkipWhitespace(line, ref index);
                if (index < line.Length && line[index] == '=')
                {
                    index++;
                    SkipWhitespace(line, ref index);
                    if (index < line.Length && line[index] == '"')
                    {
                        index++;
                        var closed = false;
                        while (index < line.Length)
                        {
                            var c = line[index++];
                            if (c == '"') { closed = true; break; }
                            if (c == '\\' && index++ >= line.Length) throw new InvalidDataException("Invalid quoted extension.");
                        }
                        if (!closed) throw new InvalidDataException("Unclosed chunk extension.");
                    }
                    else ReadToken(line, ref index);
                }
            }
            return size;
        }

        private static void SkipWhitespace(string line, ref int index)
        {
            while (index < line.Length && (line[index] == ' ' || line[index] == '\t')) index++;
        }

        private static void ReadToken(string line, ref int index)
        {
            var start = index;
            while (index < line.Length && HttpRequestFraming.IsTokenCharacter(line[index])) index++;
            if (index == start) throw new InvalidDataException("Missing chunk extension token.");
        }

        private static void ValidateTrailer(string line)
        {
            var colon = EmbedIO.Internal.StringOperations.IndexOfOrdinal(line, ':');
            if (colon <= 0) throw new InvalidDataException("Invalid request trailer.");
            for (var i = 0; i < colon; i++)
                if (!HttpRequestFraming.IsTokenCharacter(line[i])) throw new InvalidDataException("Invalid request trailer name.");
            var name = line.Substring(0, colon);
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Forbidden request trailer.");
            // Trailers are validated and consumed, never merged into routing/authentication headers.
        }
    }
}
