using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;

namespace EmbedIO
{
    /// <summary>
    /// Advertises and validates the media types accepted by a QUERY resource (RFC 10008).
    /// </summary>
    /// <remarks>Apply this policy before writing the response. The handler remains responsible
    /// for validating query content and providing safe, idempotent processing.</remarks>
    public sealed class QueryFormatPolicy
    {
        private readonly Format[] _formats;

        /// <summary>Creates a resource policy from HTTP media ranges and optional parameters.</summary>
        /// <param name="supportedMediaTypes">Supported media ranges. Wildcards are limited to type/* and */*.</param>
        /// <exception cref="ArgumentException">A range cannot be represented in Accept-Query.</exception>
        /// <exception cref="ArgumentNullException">The array is null.</exception>
        public QueryFormatPolicy(params string[] supportedMediaTypes)
        {
            if (supportedMediaTypes is null) throw new ArgumentNullException(nameof(supportedMediaTypes));
            if (supportedMediaTypes.Length == 0) throw new ArgumentException("At least one QUERY format is required.", nameof(supportedMediaTypes));
            _formats = new Format[supportedMediaTypes.Length];
            var structured = new StringBuilder();
            var accept = new StringBuilder();
            for (var index = 0; index < supportedMediaTypes.Length; index++)
            {
                if (!MediaTypeHeaderValue.TryParse(supportedMediaTypes[index], out var parsed) || parsed?.MediaType is null)
                    throw new ArgumentException("Invalid QUERY media range.", nameof(supportedMediaTypes));
                Split(parsed.MediaType, out var type, out var subtype);
                if (!ValidRange(type, subtype)) throw new ArgumentException("Unsupported QUERY media wildcard.", nameof(supportedMediaTypes));
                var parameters = new List<KeyValuePair<string, string>>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (index != 0) { structured.Append(", "); accept.Append(", "); }
                Quote(structured, AsciiLower(parsed.MediaType));
                accept.Append(parsed.ToString());
                foreach (var parameter in parsed.Parameters)
                {
                    var key = AsciiLower(parameter.Name);
                    if (!ValidKey(key) || !names.Add(key) || parameter.Value is null)
                        throw new ArgumentException("Invalid or repeated QUERY media parameter.", nameof(supportedMediaTypes));
                    var value = Unquote(parameter.Value);
                    parameters.Add(new KeyValuePair<string, string>(key, value));
                    structured.Append(';').Append(key).Append('=');
                    Quote(structured, value);
                }
                _formats[index] = new Format(type, subtype, parameters.ToArray());
            }
            AcceptQueryHeaderValue = structured.ToString();
            AcceptHeaderValue = accept.ToString();
        }

        /// <summary>Gets the immutable Structured Fields Accept-Query list.</summary>
        public string AcceptQueryHeaderValue { get; }

        /// <summary>Gets the ordinary HTTP Accept media-range list used with 415 responses.</summary>
        public string AcceptHeaderValue { get; }

        /// <summary>Checks an actual request Content-Type against this policy.</summary>
        /// <param name="contentType">An HTTP Content-Type field value.</param>
        /// <returns>True for a valid matching media type, including configured parameter constraints.</returns>
        public bool IsSupported(string? contentType)
            => TryRequest(contentType, out var parsed, out var parameters) && Matches(parsed ?? throw new InvalidOperationException("Missing parsed media type."), parameters);

        /// <summary>Advertises this resource's QUERY formats and validates exact QUERY requests.</summary>
        /// <param name="context">The resource's HTTP context.</param>
        /// <exception cref="ArgumentNullException">The context is null.</exception>
        /// <exception cref="HttpException">QUERY Content-Type is invalid (400) or unsupported (415).</exception>
        /// <remarks>Other methods retain their request behavior. Apply only at the intended resource;
        /// this method does not route requests, read content, evaluate queries or implement caching.</remarks>
        public void Apply(IHttpContext context)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));
            context.Response.Headers[HttpHeaderNames.AcceptQuery] = AcceptQueryHeaderValue;
            if (!string.Equals(context.Request.HttpMethod, "QUERY", StringComparison.Ordinal)) return;
            if (!TryRequest(context.Request.ContentType, out var parsed, out var parameters))
                throw HttpException.BadRequest("QUERY requires an unambiguous request media type.");
            if (Matches(parsed ?? throw new InvalidOperationException("Missing parsed QUERY media type."), parameters)) return;
            context.Response.Headers[HttpHeaderNames.Accept] = AcceptHeaderValue;
            throw new HttpException(415, "Unsupported QUERY content type.");
        }

        private static bool TryRequest(string? value, out MediaTypeHeaderValue? parsed, out bool parameters)
        {
            parsed = null;
            parameters = false;
            if (!MediaTypeHeaderValue.TryParse(value, out var result) || result?.MediaType is null) return false;
            if (result.MediaType.IndexOf("*", StringComparison.Ordinal) >= 0) return false;
            parameters = value != null && value.IndexOf(";", StringComparison.Ordinal) >= 0;
            if (parameters)
            {
                HashSet<string>? names = result.Parameters.Count > 1 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                foreach (var parameter in result.Parameters)
                    if (parameter.Value is null || (names != null && !names.Add(parameter.Name))) return false;
            }
            parsed = result;
            return true;
        }
        private bool Matches(MediaTypeHeaderValue request, bool parameters)
        {
            var mediaType = request.MediaType ?? throw new InvalidOperationException("Missing media type.");
            var slash = mediaType.IndexOf("/", StringComparison.Ordinal);
            var subtypeLength = mediaType.Length - slash - 1;
            foreach (var format in _formats)
            {
                if (format.Type != "*" && (format.Type.Length != slash || string.Compare(format.Type, 0, mediaType, 0, slash, StringComparison.OrdinalIgnoreCase) != 0)) continue;
                if (format.Subtype != "*" && (format.Subtype.Length != subtypeLength || string.Compare(format.Subtype, 0, mediaType, slash + 1, subtypeLength, StringComparison.OrdinalIgnoreCase) != 0)) continue;
                if (format.Parameters.Length != 0 && !parameters) continue;
                var matches = true;
                foreach (var required in format.Parameters)
                {
                    var found = false;
                    foreach (var candidate in request.Parameters)
                    {
                        var value = candidate.Value;
                        if (!string.Equals(candidate.Name, required.Key, StringComparison.OrdinalIgnoreCase) || value is null) continue;
                        found = string.Equals(required.Value, Unquote(value), required.Key == "charset" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                        break;
                    }
                    if (!found) { matches = false; break; }
                }
                if (matches) return true;
            }
            return false;
        }

        private static void Split(string mediaType, out string type, out string subtype)
        {
            var slash = mediaType.IndexOf("/", StringComparison.Ordinal);
            type = mediaType.Substring(0, slash);
            subtype = mediaType.Substring(slash + 1);
        }
        private static bool ValidRange(string type, string subtype)
            => (type == "*" && subtype == "*")
                || (type.IndexOf("*", StringComparison.Ordinal) < 0 && (subtype == "*" || subtype.IndexOf("*", StringComparison.Ordinal) < 0));
        private static string AsciiLower(string value)
        {
            char[]? result = null;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character is < 'A' or > 'Z') continue;
                result ??= value.ToCharArray();
                result[index] = (char)(character + 32);
            }
            return result is null ? value : new string(result);
        }
        private static bool ValidKey(string key)
        {
            if (key.Length == 0 || !(key[0] is >= 'a' and <= 'z' or '*')) return false;
            foreach (var character in key)
                if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' or '*')) return false;
            return true;
        }
        private static string Unquote(string value)
        {
            if (value.Length < 2 || value[0] != '"') return value;
            if (value.IndexOf("\\", StringComparison.Ordinal) < 0) return value.Substring(1, value.Length - 2);
            var result = new StringBuilder();
            for (var index = 1; index < value.Length - 1; index++)
            {
                if (value[index] == '\\') index++;
                result.Append(value[index]);
            }
            return result.ToString();
        }
        private static void Quote(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (var character in value)
            {
                if (character < 32 || character > 126) throw new ArgumentException("Accept-Query string values must be printable ASCII.");
                if (character is '"' or '\\') output.Append('\\');
                output.Append(character);
            }
            output.Append('"');
        }
        private sealed class Format
        {
            internal Format(string type, string subtype, KeyValuePair<string, string>[] parameters)
            {
                Type = type;
                Subtype = subtype;
                Parameters = parameters;
            }
            internal string Type { get; }
            internal string Subtype { get; }
            internal KeyValuePair<string, string>[] Parameters { get; }
        }
    }
}
