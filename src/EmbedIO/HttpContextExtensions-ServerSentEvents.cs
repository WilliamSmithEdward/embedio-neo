using System;

namespace EmbedIO
{
    partial class HttpContextExtensions
    {
        /// <summary>Prepares an unbuffered server-sent event response and returns its writer.</summary>
        /// <param name="this">The context whose response has not yet been written.</param>
        /// <returns>A writer that frames and flushes events without owning the response stream.</returns>
        /// <remarks>
        /// Call once before writing the response. Do not combine with other response writers,
        /// compression or a fixed content length. This opt-in helper sets text/event-stream,
        /// UTF-8, chunked transfer and Cache-Control: no-cache. It does not alter listener defaults.
        /// Configure IgnoreWriteExceptions before listener startup to receive transport failures.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The context is null.</exception>
        public static ServerSentEventWriter OpenEventStream(this IHttpContext @this)
        {
            if (@this == null) throw new ArgumentNullException(nameof(@this));
            var response = @this.Response;
            response.ContentType = "text/event-stream";
            response.ContentEncoding = WebServer.Utf8NoBomEncoding;
            response.SendChunked = true;
            response.Headers["Cache-Control"] = "no-cache";
            return new ServerSentEventWriter(response.OutputStream, @this.CancellationToken);
        }
    }
}
