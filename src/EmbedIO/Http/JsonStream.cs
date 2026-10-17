using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO
{
    /// <summary>Immutable limits and flush policy for an explicitly streamed JSON response.</summary>
    public sealed class JsonStreamOptions
    {
        /// <summary>Creates a streaming policy. Limits apply to each serialized record, not the complete response.</summary>
        /// <param name="maxItemBytes">Maximum UTF-8 bytes per record, from one byte through 64 MiB.</param>
        /// <param name="flushEvery">Flush after this many records. The final record is always flushed.</param>
        /// <param name="preferCompression">Whether to prefer negotiated compression, which may add buffering.</param>
        public JsonStreamOptions(int maxItemBytes = 1024 * 1024, int flushEvery = 1, bool preferCompression = false)
        {
            if (maxItemBytes < 1 || maxItemBytes > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxItemBytes));
            if (flushEvery < 1) throw new ArgumentOutOfRangeException(nameof(flushEvery));
            MaxItemBytes = maxItemBytes;
            FlushEvery = flushEvery;
            PreferCompression = preferCompression;
        }

        /// <summary>Gets the maximum serialized UTF-8 record size.</summary>
        public int MaxItemBytes { get; }
        /// <summary>Gets the number of records between explicit flushes.</summary>
        public int FlushEvery { get; }
        /// <summary>Gets whether negotiated compression is preferred.</summary>
        public bool PreferCompression { get; }
    }

    /// <summary>Creates explicit streaming responses with source-generated JSON metadata.</summary>
    public static class JsonStream
    {
        /// <summary>Streams a JSON array without collecting the source sequence.</summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="source">The cooperatively cancellable source.</param>
        /// <param name="typeInfo">Metadata for one item; use a generated JSON context for Native AOT.</param>
        /// <param name="options">Optional immutable limits and flush policy.</param>
        /// <returns>An explicitly streamed array response.</returns>
        public static JsonArrayStream<T> Array<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo, JsonStreamOptions? options = null)
            => new(source, typeInfo, options ?? new JsonStreamOptions());

        /// <summary>Streams one compact JSON value per LF-terminated line.</summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="source">The cooperatively cancellable source.</param>
        /// <param name="typeInfo">Metadata for one item. Indentation is ignored; converters must not emit literal CR/LF.</param>
        /// <param name="options">Optional immutable limits and flush policy.</param>
        /// <returns>An explicitly streamed NDJSON response.</returns>
        public static NdjsonStream<T> Ndjson<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo, JsonStreamOptions? options = null)
            => new(source, typeInfo, options ?? new JsonStreamOptions());
    }

    /// <summary>An opt-in response understood by controller bindings, or written directly with <see cref="WriteAsync"/>.</summary>
    public abstract class JsonStreamResult
    {
        internal JsonStreamResult() { }
        /// <summary>Writes the sequence and awaits producer cleanup. Failures after commitment abort the response.</summary>
        /// <param name="context">The response context.</param>
        /// <param name="cancellationToken">Additional cooperative cancellation.</param>
        /// <returns>The complete response lifetime.</returns>
        public abstract Task WriteAsync(IHttpContext context, CancellationToken cancellationToken = default);
    }

    /// <summary>A streamed JSON array returned by <see cref="JsonStream.Array{T}"/>.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    public sealed class JsonArrayStream<T> : JsonStreamResult
    {
        private readonly JsonSequenceWriter<T> _writer;
        internal JsonArrayStream(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo, JsonStreamOptions options)
            => _writer = new(source, typeInfo, options, false);
        /// <inheritdoc/>
        public override Task WriteAsync(IHttpContext context, CancellationToken cancellationToken = default) => _writer.WriteAsync(context, cancellationToken);
    }

    /// <summary>A streamed NDJSON response returned by <see cref="JsonStream.Ndjson{T}"/>.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    public sealed class NdjsonStream<T> : JsonStreamResult
    {
        private readonly JsonSequenceWriter<T> _writer;
        internal NdjsonStream(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo, JsonStreamOptions options)
            => _writer = new(source, typeInfo, options, true);
        /// <inheritdoc/>
        public override Task WriteAsync(IHttpContext context, CancellationToken cancellationToken = default) => _writer.WriteAsync(context, cancellationToken);
    }

}
