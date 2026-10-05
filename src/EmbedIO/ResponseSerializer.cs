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
        public static Task Json(IHttpContext context, object? data) => WriteJsonAsync(context, data, false, null);

        /// <summary>Creates a JSON serializer with custom .NET options.</summary>
        public static ResponseSerializerCallback Json(JsonSerializerOptions options) => Json(false, options);

        /// <summary>Creates a JSON serializer with optional response buffering.</summary>
        public static ResponseSerializerCallback Json(bool bufferResponse)
            => (context, data) => WriteJsonAsync(context, data, bufferResponse, null);

        /// <summary>Creates a JSON serializer with custom options and optional buffering.</summary>
        public static ResponseSerializerCallback Json(bool bufferResponse, JsonSerializerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var snapshot = new JsonSerializerOptions(options);
            return (context, data) => WriteJsonAsync(context, data, bufferResponse, snapshot);
        }

        private static Task WriteJsonAsync(IHttpContext context, object? data, bool bufferResponse, JsonSerializerOptions? options)
        {
            context.Response.ContentType = MimeType.Json;
            context.Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
            var json = options == null ? Serialization.Json.Serialize(data) : Serialization.Json.Serialize(data, options);
            return None(bufferResponse)(context, json);
        }

        /// <summary>Sends strings and bytes unchanged, optionally buffering the response.</summary>
        public static ResponseSerializerCallback None(bool bufferResponse)
            => bufferResponse ? BufferingBaseSerializer : ChunkedEncodingBaseSerializer;

        private static ResponseSerializerCallback GetBaseSerializer(bool bufferResponse)
            => async (context, data) => {
                if (data is null)
                {
                    return;
                }

                var isBinaryResponse = data is byte[];

                if (!context.TryDetermineCompression(context.Response.ContentType, out var preferCompression))
                {
                    preferCompression = true;
                }

                if (isBinaryResponse)
                {
                    var responseBytes = (byte[])data;
                    using var stream = context.OpenResponseStream(bufferResponse, preferCompression);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length).ConfigureAwait(false);
                }
                else
                {
                    var responseString = data is string stringData ? stringData : data.ToString() ?? string.Empty;
                    using var text = context.OpenResponseText(context.Response.ContentEncoding, bufferResponse, preferCompression);
                    await text.WriteAsync(responseString).ConfigureAwait(false);
                }
            };
    }
}
