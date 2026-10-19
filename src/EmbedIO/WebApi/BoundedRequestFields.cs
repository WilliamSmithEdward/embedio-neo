using System;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.WebApi
{
    internal static class BoundedRequestFields
    {
        internal const int MaxEncodedBytes = 262144;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly object QueryKey = new();
        private static readonly object FormKey = new();

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0057:Use range operator",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index."
        )]
        internal static NameValueCollection Query(IHttpContext context)
        {
            if (context.Items.TryGetValue(QueryKey, out var cached)) return Cached(cached);

            var target = context.Request.RawTarget;
            var question = StringOperations.IndexOfOrdinal(target, '?');
            var source = question < 0 ? string.Empty : target.Substring(question + 1);
            var result = Parse(source);
            context.Items[QueryKey] = result;
            return result;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1835:Prefer the memory-based overloads for ReadAsync and WriteAsync",
            Justification = "The shared implementation targets netstandard2.0, whose Stream API provides the byte-array async overloads."
        )]
        internal static async Task<NameValueCollection> FormAsync(IHttpContext context)
        {
            if (context.Items.TryGetValue(FormKey, out var cached)) return Cached(cached);

            using var body = context.OpenRequestStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];

            while (true)
            {
                var count = await body
                    .ReadAsync(
                        chunk,
                        0,
                        Math.Min(chunk.Length, MaxEncodedBytes + 1 - (int)buffer.Length),
                        context.CancellationToken
                    )
                    .ConfigureAwait(false);

                if (count == 0) break;

                if (buffer.Length + count > MaxEncodedBytes)
                    throw RequestBindingValues.Failure(string.Empty, "The encoded form limit was exceeded.");

                buffer.Write(chunk, 0, count);
            }

            string source;

            try
            {
                var encoding = Encoding.GetEncoding(
                    context.Request.ContentEncoding.CodePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback
                );
                source = encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }

            catch (DecoderFallbackException)
            {
                throw RequestBindingValues.Failure(string.Empty, "The form encoding is invalid.");
            }

            var result = Parse(source);
            context.Items[FormKey] = result;
            return result;
        }

        private static NameValueCollection Cached(object? value) =>
            value as NameValueCollection
                ?? throw new InvalidOperationException("Invalid cached request binding fields.");

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0057:Use range operator",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index."
        )]
        internal static NameValueCollection Parse(string source)
        {
            if (source.Length > MaxEncodedBytes)
                throw RequestBindingValues.Failure(string.Empty, "The encoded fields limit was exceeded.");

            var output = new LockableNameValueCollection(StringComparer.OrdinalIgnoreCase);
            var bounds = new RequestBindingValues();
            var position = 0;
            var pairs = 0;

            while (position < source.Length)
            {
                if (++pairs > RequestBindingValues.MaxValues)
                    throw RequestBindingValues.Failure(string.Empty, "Too many request values.");

                var end = source.IndexOf('&', position);

                if (end < 0) end = source.Length;

                var equals = source.IndexOf('=', position, end - position);
                var key = Decode(source.Substring(position, (equals < 0 ? end : equals) - position), 128);
                var value = equals < 0
                    ? string.Empty
                    : Decode(source.Substring(equals + 1, end - equals - 1), RequestBindingValues.MaxLength);
                bounds.Add(key, value);
                output.Add(key, value);
                position = end + 1;

                if (position == source.Length)
                    throw RequestBindingValues.Failure(string.Empty, "An empty request field is invalid.");
            }

            output.MakeReadOnly();
            return output;
        }

        private static string Decode(string encoded, int maximumLength)
        {
            if (encoded.Length > maximumLength * 9)
                throw RequestBindingValues.Failure(string.Empty, "The encoded field limit was exceeded.");

            byte[] bytes;

            try
            {
                bytes = Utf8.GetBytes(encoded);
            }

            catch (EncoderFallbackException)
            {
                throw RequestBindingValues.Failure(string.Empty, "The field encoding is invalid.");
            }

            var written = 0;

            for (var i = 0; i < bytes.Length; i++)
            {
                var value = bytes[i];

                if (value == '%')
                {
                    if (i + 2 >= bytes.Length)
                        throw RequestBindingValues.Failure(string.Empty, "Invalid percent escape.");

                    var high = Hex(bytes[++i]);
                    var low = Hex(bytes[++i]);

                    if (high < 0 || low < 0)
                        throw RequestBindingValues.Failure(string.Empty, "Invalid percent escape.");

                    value = (byte)((high << 4) | low);
                }

                else if (value == '+') value = (byte)' ';

                bytes[written++] = value;
            }

            try
            {
                var result = Utf8.GetString(bytes, 0, written);

                if (result.Length > maximumLength)
                    throw RequestBindingValues.Failure(string.Empty, "The field length limit was exceeded.");

                return result;
            }

            catch (DecoderFallbackException)
            {
                throw RequestBindingValues.Failure(string.Empty, "The field encoding is invalid.");
            }
        }

        private static int Hex(byte value) =>
            value is >= (byte)'0'
                and <= (byte)'9' ? value - '0' : value is >= (byte)'A'
                and <= (byte)'F' ? value - 'A' + 10 : value is >= (byte)'a'
                and <= (byte)'f' ? value - 'a' + 10 : -1;
    }
}
