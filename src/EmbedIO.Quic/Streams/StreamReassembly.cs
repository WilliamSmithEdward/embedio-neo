using System.Buffers;

namespace EmbedIO.Quic
{
    // A fixed logical receive window backed only by occupied pages. Returned pages
    // are cleared; final-size history and buffered overlap validation survive reuse.
    internal sealed class StreamReassembly(int capacity, ArrayPool<byte>? pool = null) : IDisposable
    {
        internal const ulong MaximumOffset = (1UL << 62) - 1;
        private const int PageSize = 1024;
        private const int BitmapBytes = PageSize / 8;

        private struct Page
        {
            internal byte[]? Bytes;
            internal int Count;
        }

        private readonly int _capacity = capacity >= 0
            ? capacity
            : throw new ArgumentOutOfRangeException(nameof(capacity));
        private readonly ArrayPool<byte> _pool = pool ?? ArrayPool<byte>.Shared;
        private Page[]? _pages;
        private ulong _extent;
        private bool _disposed;

        internal ulong Delivered { get; private set; }
        internal ulong? FinalSize { get; private set; }
        internal bool Complete => FinalSize == Delivered;
        internal int RetainedCapacity => _pages?.Sum(p => p.Bytes?.Length ?? 0) ?? 0;

        internal void Add(ulong offset, ReadOnlySpan<byte> bytes, bool final = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (offset > MaximumOffset || (ulong)bytes.Length > MaximumOffset - offset)
                throw new InvalidDataException("Invalid stream offset.");

            var end = offset + (ulong)bytes.Length;

            if (FinalSize.HasValue && (end > FinalSize.Value || final && end != FinalSize.Value))
                throw new InvalidDataException("Conflicting final size.");

            if (final && end < _extent) throw new InvalidDataException("Final size precedes received bytes.");

            if (end > Delivered && end - Delivered > (ulong)_capacity)
                throw new InvalidDataException("Stream receive window exceeded.");

            if (final) FinalSize = end;

            _extent = Math.Max(_extent, end);
            var start = offset < Delivered ? (int)Math.Min((ulong)bytes.Length, Delivered - offset) : 0;

            while (start < bytes.Length)
            {
                _pages ??= new Page[(_capacity - 1) / PageSize + 1];
                var ring = (int)((offset + (ulong)start) % (ulong)_capacity);
                var local = ring % PageSize;
                var take = Math.Min(bytes.Length - start, Math.Min(PageSize - local, _capacity - ring));
                ref var page = ref _pages[ring / PageSize];

                if (page.Bytes == null)
                {
                    page.Bytes = _pool.Rent(PageSize + BitmapBytes);
                    page.Bytes
                        .AsSpan(PageSize, BitmapBytes)
                        .Clear();
                }

                var buffer = page.Bytes;

                for (var i = 0; i < take; i++)
                {
                    var index = local + i;
                    var mark = PageSize + (index >> 3);
                    var mask = (byte)(1 << (index & 7));

                    if ((buffer[mark] & mask) != 0)
                    {
                        if (buffer[index] != bytes[start + i])
                            throw new InvalidDataException("Conflicting overlapping data.");
                    }

                    else
                    {
                        buffer[mark] |= mask;
                        page.Count++;
                    }

                    buffer[index] = bytes[start + i];
                }

                start += take;
            }
        }

        internal byte[] Consume(ulong maximum = MaximumOffset)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_pages == null || maximum <= Delivered) return [];

            var count = 0;
            var limit = (int)Math.Min((ulong)_capacity, maximum - Delivered);

            while (count < limit)
            {
                var ring = (int)((Delivered + (ulong)count) % (ulong)_capacity);
                var local = ring % PageSize;
                var buffer = _pages[ring / PageSize].Bytes;

                if (buffer == null) break;

                var take = Math.Min(limit - count, Math.Min(PageSize - local, _capacity - ring));
                var contiguous = 0;

                while (contiguous < take)
                {
                    var index = local + contiguous;

                    if ((buffer[PageSize + (index >> 3)] & (1 << (index & 7))) == 0) break;

                    contiguous++;
                }

                count += contiguous;

                if (contiguous != take) break;
            }

            if (count == 0) return [];

            var result = new byte[count];
            var copied = 0;

            while (copied < count)
            {
                var ring = (int)((Delivered + (ulong)copied) % (ulong)_capacity);
                var local = ring % PageSize;
                ref var page = ref _pages[ring / PageSize];
                var buffer = page.Bytes ?? throw new InvalidOperationException();
                var take = Math.Min(count - copied, Math.Min(PageSize - local, _capacity - ring));
                buffer
                    .AsSpan(local, take)
                    .CopyTo(result.AsSpan(copied));
                page.Count -= take;

                if (page.Count == 0)
                {
                    page.Bytes = null;
                    _pool.Return(buffer, clearArray: true);
                }

                else
                {
                    buffer
                        .AsSpan(local, take)
                        .Clear();

                    for (var i = 0; i < take; i++)
                    {
                        var index = local + i;
                        buffer[PageSize + (index >> 3)] &= (byte)~(1 << (index & 7));
                    }
                }

                copied += take;
            }

            Delivered += (ulong)count;
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            if (_pages != null)
                foreach (var page in _pages)
                    if (page.Bytes != null) _pool.Return(page.Bytes, clearArray: true);

            _pages = null;
        }
    }
}
