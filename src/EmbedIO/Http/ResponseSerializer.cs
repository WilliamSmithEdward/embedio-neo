using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Standard response serializers.</summary>
    public static class ResponseSerializer
    {
        /// <summary>The default JSON serializer.</summary>
        public static readonly ResponseSerializerCallback Default = Json;
        private static readonly ResponseSerializerCallback ChunkedEncodingBaseSerializer = GetBaseSerializer(false);
        private static readonly ResponseSerializerCallback BufferingBaseSerializer = GetBaseSerializer(true);

        /// <summary>Serializes a response using EmbedIO's .NET JSON defaults.</summary>
        /// <remarks>The defaults resolve metadata by reflection. Where that is disabled, as in trimmed
        /// and Native AOT applications, this throws <see cref="NotSupportedException"/>; use
        /// <see cref="Json(JsonSerializerOptions)"/> with source-generated options instead.</remarks>
        public static Task Json(IHttpContext context, object? data)
        {
            if (context is null) throw new System.NullReferenceException();

            if (!Serialization.ReflectionSerialization.IsSupported)
                throw Serialization.ReflectionSerialization.Disabled();

            return WriteJsonAsync(context, data, false, Serialization.Json.DefaultOptions());
        }

        /// <summary>Creates a JSON serializer with custom .NET options.</summary>
        public static ResponseSerializerCallback Json(JsonSerializerOptions options) => Json(false, options);

        /// <summary>Creates a JSON serializer with optional response buffering.</summary>
        public static ResponseSerializerCallback Json(bool bufferResponse) => (context, data) =>
        {
            if (!Serialization.ReflectionSerialization.IsSupported)
                throw Serialization.ReflectionSerialization.Disabled();

            return WriteJsonAsync(context, data, bufferResponse, Serialization.Json.DefaultOptions());
        };

        /// <summary>Creates a JSON serializer with custom options and optional buffering.</summary>
        public static ResponseSerializerCallback Json(bool bufferResponse, JsonSerializerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var snapshot = new JsonSerializerOptions(options);
            return (context, data) => WriteJsonAsync(context, data, bufferResponse, snapshot);
        }

        private static Task WriteJsonAsync(
            IHttpContext context,
            object? data,
            bool bufferResponse,
            JsonSerializerOptions options
        ) => WriteJsonBytesAsync(context, Serialization.Json.SerializeUtf8(data, options), bufferResponse);

        internal static Task WriteJsonBytesAsync(IHttpContext context, byte[] json, bool bufferResponse)
        {
            context.Response.ContentType = MimeType.Json;
            context.Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
            return None(bufferResponse)(context, json);
        }

        /// <summary>Sends strings and bytes unchanged, optionally buffering the response.</summary>
        public static ResponseSerializerCallback None(bool bufferResponse) =>
            bufferResponse ? BufferingBaseSerializer : ChunkedEncodingBaseSerializer;

        private static ResponseSerializerCallback GetBaseSerializer(bool bufferResponse) => async (context, data) =>
        {
            if (data is null)
            {
                return;
            }

            var isBinaryResponse = data is byte[];

            // Compression preferences are keyed by media type; parameters such as charset are not part of it.
            if (
                !context.TryDetermineCompression(
                    MimeType.StripParameters(context.Response.ContentType) ?? MimeType.Default,
                    out var preferCompression
                )
            )
            {
                preferCompression = true;
            }

            // On the .NET 10 asset the response ends asynchronously, so a compressor's final
            // block and the terminator do not take a synchronous transport write.
            if (isBinaryResponse)
            {
                var responseBytes = (byte[])data;
#if NET10_0_OR_GREATER
                await using var stream = context.OpenResponseStream(bufferResponse, preferCompression);
#else
                using var stream = context.OpenResponseStream(bufferResponse, preferCompression);
#endif
                await stream
                    .WriteAsync(responseBytes, 0, responseBytes.Length)
                    .ConfigureAwait(false);
            }

            else
            {
                var responseString = data is string stringData ? stringData : data.ToString() ?? string.Empty;

                if (context is Caching.OutputCacheCapture capture)
                {
                    await WriteCapturedTextAsync(
                        capture,
                        responseString,
                        bufferResponse,
                        preferCompression
                    ).ConfigureAwait(false);
                    return;
                }
#if NET10_0_OR_GREATER
                await using var text = context.OpenResponseText(
                    context.Response.ContentEncoding,
                    bufferResponse,
                    preferCompression
                );
#else
                using var text = context.OpenResponseText(
                    context.Response.ContentEncoding,
                    bufferResponse,
                    preferCompression
                );
#endif
                await text
                    .WriteAsync(responseString)
                    .ConfigureAwait(false);
            }
        };

        private static async Task WriteCapturedTextAsync(
            Caching.OutputCacheCapture capture,
            string text,
            bool buffered,
            bool preferCompression
        )
        {
            var writer = capture.OpenResponseText(capture.Response.ContentEncoding, buffered, preferCompression);

            try
            {
                await writer
                    .WriteAsync(text)
                    .ConfigureAwait(false);
            }

            finally
            {
                // A finite serializer's completion flush is different from an application streaming flush.
                capture.CompletingText = true;

                try
                {
#if NET10_0_OR_GREATER
                    await writer
                        .DisposeAsync()
                        .ConfigureAwait(false);
#else
                    writer.Dispose();
#endif
                }

                finally
                {
                    capture.CompletingText = false;
                }
            }
        }
    }
}
