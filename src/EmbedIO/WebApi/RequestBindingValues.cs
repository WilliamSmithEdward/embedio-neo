using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json.Nodes;
using EmbedIO.Internal;
using EmbedIO.Problems;
using EmbedIO.Utilities;

namespace EmbedIO.WebApi
{
    internal sealed class RequestBindingValues
    {
        internal const int MaxFields = 128;
        internal const int MaxValues = 1024;
        internal const int MaxLength = 4096;
        internal const int MaxBytes = 65536;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Dictionary<string, List<string>> _values;
        private int _count;
        private int _bytes;

        internal RequestBindingValues(bool cookies = false) =>
            _values = new Dictionary<string, List<string>>(
                cookies ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase
            );

        internal IEnumerable<string> Names => _values.Keys;

        internal static string CheckName(string name)
        {
            name = Validate.NotNull(nameof(name), name);

            if (name.Length == 0 || name.Length > 128 || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Field names must contain 1–128 characters.", nameof(name));

            foreach (var c in name)
                if (char.IsControl(c))
                    throw new ArgumentException("Field names cannot contain controls.", nameof(name));

            try
            {
                Utf8.GetByteCount(name);
            }

            catch (EncoderFallbackException)
            {
                throw new ArgumentException("Field names must be valid Unicode.", nameof(name));
            }

            return name;
        }

        internal static string CheckToken(string name)
        {
            name = CheckName(name);

            foreach (var c in name)
                if (c > 126 || !(char.IsLetterOrDigit(c) || StringOperations.IndexOfOrdinal("!#$%&'*+-.^_`|~", c) >= 0))
                    throw new ArgumentException("A header or cookie name must be an ASCII token.", nameof(name));

            return name;
        }

        internal static RequestBindingValues Read(NameValueCollection data)
        {
            var result = new RequestBindingValues();

            if (data.Count > MaxFields) throw Failure(string.Empty, "Too many request fields.");

            foreach (var key in data.AllKeys)
            {
                if (key == null) throw Failure(string.Empty, "A field name is missing.");

                var values = data.GetValues(key);

                if (values == null) result.Add(key, string.Empty);

                else foreach (var value in values) result.Add(key, value ?? string.Empty);
            }

            return result;
        }

        internal void Add(string name, string value)
        {
            try
            {
                name = CheckName(name);
            }

            catch (ArgumentException)
            {
                throw Failure(string.Empty, "Invalid field name.");
            }

            if (++_count > MaxValues || value.Length > MaxLength)
                throw Failure(name, "The request binding limit was exceeded.");

            try
            {
                _bytes = checked(_bytes + Utf8.GetByteCount(name) + Utf8.GetByteCount(value));
            }

            catch (EncoderFallbackException)
            {
                throw Failure(name, "The field is not valid Unicode.");
            }

            if (_bytes > MaxBytes) throw Failure(name, "The request binding text limit was exceeded.");

            if (!_values.TryGetValue(name, out var values))
            {
                if (_values.Count >= MaxFields) throw Failure(string.Empty, "Too many request fields.");

                values = [];
                _values.Add(name, values);
            }

            values.Add(value);
        }

        internal string[]? Get(
            string name,
            bool required,
            bool many,
            RequestDuplicateValues duplicateValues,
            string path
        )
        {
            if (!_values.TryGetValue(name, out var values))
            {
                if (required) throw Failure(path, "The field is required.");

                return null;
            }

            if (many) return [.. values];

            if (values.Count > 1 && duplicateValues == RequestDuplicateValues.Reject)
                throw Failure(path, "Multiple scalar values are not allowed.");

            return [values[duplicateValues == RequestDuplicateValues.Last ? values.Count - 1 : 0]];
        }

        internal static void CheckDuplicates(RequestDuplicateValues value)
        {
            if (value < RequestDuplicateValues.Reject || value > RequestDuplicateValues.Last)
                throw new ArgumentOutOfRangeException(nameof(value));
        }

        internal static ProblemDetailsException Failure(string path, string message) =>
            new(
                new ProblemDetails(400) { Title = "Request binding failed." }.WithExtension(
                    "errors",
                    new JsonObject { [path] = new JsonArray((JsonNode)JsonValue.Create(message)) }
                )
            );
    }
}
