using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Caching;
using EmbedIO.Security;

namespace EmbedIO
{
    partial class HttpResults
    {
        /// <summary>Returns a private file chosen by the application, with conditional/range support.</summary>
        /// <remarks>The path is trusted application input, never a raw client filename. The file opens at execution and must remain stable while sending.</remarks>
        public static FileHttpResult File(
            string path,
            string contentType,
            string? downloadName = null,
            string? entityTag = null,
            DateTimeOffset? lastModified = null
        ) => new(path, contentType, downloadName, entityTag, lastModified);

        /// <summary>Returns a defensive byte snapshot with conditional/range support.</summary>
        public static FileHttpResult Bytes(
            byte[] value,
            string contentType = "application/octet-stream",
            string? downloadName = null,
            string? entityTag = null,
            DateTimeOffset? lastModified = null
        ) => new(value, contentType, downloadName, entityTag, lastModified);

        /// <summary>Transfers one readable stream. Seekable streams use conditional/range handling; others send from their current position.</summary>
        /// <remarks>Ownership transfers on successful construction unless leaveOpen is true. Execute once; dispose the result if never executed. Do not dispose it during execution.</remarks>
        public static StreamHttpResult Stream(
            Stream value,
            string contentType = "application/octet-stream",
            string? downloadName = null,
            bool leaveOpen = false
        ) => new(value, contentType, downloadName, leaveOpen);

        internal static string? Disposition(string? name)
        {
            if (name == null) return null;

            if (name.Length == 0 || name.Length > 255 || name == "." || name == "..")
                throw new ArgumentException("Invalid download filename.", nameof(name));

            foreach (var c in name)
                if (char.IsControl(c) || c == '/' || c == '\\')
                    throw new ArgumentException("Use a filename without path separators or controls.", nameof(name));

            return "attachment; filename*=UTF-8''" + Uri.EscapeDataString(name);
        }
    }

    /// <summary>A lazily opened file or immutable byte snapshot with existing precondition and byte-range behavior.</summary>
    public sealed class FileHttpResult : HttpResult
    {
        private readonly Func<Stream> _open;
        private readonly string? _entityTag;
        private readonly DateTimeOffset? _lastModified;

        internal FileHttpResult(string path, string contentType, string? name, string? tag, DateTimeOffset? modified)
            : base(200, HttpResults.MediaType(contentType), disposition: HttpResults.Disposition(name))
        {
            path = global::EmbedIO.Utilities.Validate.NotNull(nameof(path), path);

            var fullPath = Path.GetFullPath(path);
            _open = () => new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            _entityTag = tag;
            _lastModified = modified;
        }

        internal FileHttpResult(byte[] bytes, string contentType, string? name, string? tag, DateTimeOffset? modified)
            : base(200, HttpResults.MediaType(contentType), disposition: HttpResults.Disposition(name))
        {
            bytes = global::EmbedIO.Utilities.Validate.NotNull(nameof(bytes), bytes);

            var snapshot = (byte[])bytes.Clone();
            _open = () => new MemoryStream(snapshot, false);
            _entityTag = tag;
            _lastModified = modified;
        }

        internal static void PrepareStreaming(IHttpContext context)
        {
            if (SecurityHeadersState.IsRecording(context))
                throw new InvalidOperationException("Stream and file results cannot be recorded for idempotency.");

            if (context is OutputCacheCapture capture) capture.Disabled = true;
        }

        internal override async Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer)
        {
            PrepareStreaming(context);
            using var content = _open();
            await context
                .SendRepresentationAsync(
                    content,
                    ContentType ?? MimeType.Default,
                    _entityTag,
                    _lastModified,
                    leaveOpen: true
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>A single-use stream result with explicit ownership, including abandonment before execution.</summary>
    public sealed class StreamHttpResult : HttpResult, IDisposable
    {
        private readonly Stream _stream;
        private int _disposed;
        private readonly bool _leaveOpen;

        internal StreamHttpResult(Stream content, string contentType, string? name, bool leaveOpen)
            : base(200, HttpResults.MediaType(contentType), disposition: HttpResults.Disposition(name), singleUse: true)
        {
            content = global::EmbedIO.Utilities.Validate.NotNull(nameof(content), content);

            if (!content.CanRead) throw new ArgumentException("A readable stream is required.", nameof(content));

            _stream = content;
            _leaveOpen = leaveOpen;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1513:Use ObjectDisposedException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf."
        )]
        internal override async Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer)
        {
            FileHttpResult.PrepareStreaming(context);

            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(StreamHttpResult));

            var content = _stream;

            if (content.CanSeek)
            {
                await context
                    .SendRepresentationAsync(content, ContentType ?? MimeType.Default, leaveOpen: true)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.SendChunked = true;

            if (context.Request.HttpMethod == "HEAD") return;

            var output = context.Response.OutputStream;
            await content
                .CopyToAsync(output, 65536, context.CancellationToken)
                .ConfigureAwait(false);
        }

        internal override void Release() => Dispose();

        /// <summary>Releases an owned stream once. A borrowed stream remains open.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && !_leaveOpen) _stream.Dispose();
        }
    }
}
