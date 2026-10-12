using System;
using System.Threading.Tasks;
using System.Text.Json;
using EmbedIO.Diagnostics;

namespace EmbedIO
{
    /// <summary>
    /// Provides standard request deserialization callbacks.
    /// </summary>
    public static class RequestDeserializer
    {
        /// <summary>
        /// <para>The default request deserializer used by EmbedIO.</para>
        /// <para>Equivalent to <see cref="Json{TData}(IHttpContext)"/>.</para>
        /// </summary>
        /// <typeparam name="TData">The expected type of the deserialized data.</typeparam>
        /// <param name="context">The <see cref="IHttpContext"/> whose request body is to be deserialized.</param>
        /// <returns>A <see cref="Task{TResult}">Task</see>, representing the ongoing operation,
        /// whose result will be the deserialized data.</returns>
        public static Task<TData?> Default<TData>(IHttpContext context) => Json<TData>(context);

        /// <summary>
        /// Asynchronously deserializes a request body in JSON format.
        /// </summary>
        /// <typeparam name="TData">The expected type of the deserialized data.</typeparam>
        /// <param name="context">The <see cref="IHttpContext"/> whose request body is to be deserialized.</param>
        /// <returns>A <see cref="Task{TResult}">Task</see>, representing the ongoing operation,
        /// whose result will be the deserialized data.</returns>
        /// <remarks>EmbedIO's default options resolve metadata by reflection. Where that is disabled,
        /// as in trimmed and Native AOT applications, this throws <see cref="NotSupportedException"/>;
        /// use <see cref="Json{TData}(JsonSerializerOptions)"/> with source-generated options instead.</remarks>
        public static Task<TData?> Json<TData>(IHttpContext context)
        {
            if (context is null) throw new System.NullReferenceException();
            if (!EmbedIO.Serialization.ReflectionSerialization.IsSupported) throw EmbedIO.Serialization.ReflectionSerialization.Disabled();
            return JsonInternal<TData>(context, EmbedIO.Serialization.Json.DefaultOptions());
        }

        /// <summary>
        /// Returns a <see cref="RequestDeserializerCallback{TData}">RequestDeserializerCallback</see>
        /// that will deserialize an HTTP request body in JSON format, using the specified options.
        /// </summary>
        /// <typeparam name="TData">The expected type of the deserialized data.</typeparam>
        /// <param name="options">The .NET JSON options to use, or null for EmbedIO defaults.</param>
        /// <returns>A <see cref="RequestDeserializerCallback{TData}"/> that can be used to deserialize
        /// a JSON request body.</returns>
        /// <remarks>Options whose resolver is a <c>JsonSerializerContext</c> work in trimmed and Native
        /// AOT applications. Null selects EmbedIO's reflection-based defaults.</remarks>
        public static RequestDeserializerCallback<TData?> Json<TData>(JsonSerializerOptions? options)
        {
            if (options == null) return Json<TData>;
            var snapshot = new JsonSerializerOptions(options);
            return context => JsonInternal<TData>(context, snapshot);
        }

        private static Task<TData?> JsonInternal<TData>(IHttpContext context, JsonSerializerOptions options)
            => JsonBodyAsync(context, body => EmbedIO.Serialization.Json.DeserializeWith<TData>(body, options));

        // Reads the whole body as text and deserializes it; malformed JSON becomes HTTP 400.
        internal static async Task<TData?> JsonBodyAsync<TData>(IHttpContext context, Func<string, TData?> deserialize)
        {
            string body;
            using (var reader = context.OpenRequestText())
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            try
            {
                return deserialize(body);
            }
            catch (JsonException)
            {
                $"[{context.Id}] Cannot convert JSON request body to {typeof(TData).Name}, sending 400 Bad Request..."
                    .Warn($"{nameof(RequestDeserializer)}.{nameof(Json)}");

                throw HttpException.BadRequest("Incorrect request data format.");
            }
        }
    }
}
