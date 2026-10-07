using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbedIO.Serialization
{
    // Presence in the options also identifies EmbedIO defaults after options are cloned.
    internal sealed class CompatibleJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert == typeof(bool) || typeToConvert.IsEnum
                || typeToConvert == typeof(DateTime) || typeToConvert == typeof(DateTimeOffset);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            // An application converter appended to CreateOptions takes precedence over these defaults.
            foreach (var converter in options.Converters)
            {
                if (converter is CompatibleJsonConverterFactory || !converter.CanConvert(typeToConvert)) continue;
                return converter is JsonConverterFactory factory ? factory.CreateConverter(typeToConvert, options)! : converter;
            }
            if (typeToConvert == typeof(bool)) return new BooleanConverter();
            if (typeToConvert == typeof(DateTime)) return new DateTimeConverter();
            if (typeToConvert == typeof(DateTimeOffset)) return new DateTimeOffsetConverter();
            return (JsonConverter)Activator.CreateInstance(typeof(EnumConverter<>).MakeGenericType(typeToConvert))!;
        }

        private sealed class BooleanConverter : JsonConverter<bool>
        {
            public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.True) return true;
                if (reader.TokenType == JsonTokenType.False) return false;
                if (reader.TokenType == JsonTokenType.String && bool.TryParse(reader.GetString(), out var value)) return value;
                throw new JsonException("Expected a boolean value.");
            }

            public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
        }

        private sealed class EnumConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), out var value)) return value;
                if (reader.TokenType == JsonTokenType.Number)
                {
                    var number = JsonSerializer.Deserialize(ref reader, Enum.GetUnderlyingType(typeof(T)), options)!;
                    return (T)Enum.ToObject(typeof(T), number);
                }
                throw new JsonException("Expected an enum name or number.");
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
                => JsonSerializer.Serialize(writer, Convert.ChangeType(value, Enum.GetUnderlyingType(typeof(T)), CultureInfo.InvariantCulture), options);
        }

        private sealed class DateTimeConverter : JsonConverter<DateTime>
        {
            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    if (reader.TryGetDateTime(out var value)) return value;
                    if (DateTime.TryParse(reader.GetString(), CultureInfo.CurrentCulture, DateTimeStyles.RoundtripKind, out value)) return value;
                }
                throw new JsonException("Expected a date/time value.");
            }

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue(value);
        }

        private sealed class DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
        {
            public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    if (reader.TryGetDateTimeOffset(out var value)) return value;
                    if (DateTimeOffset.TryParse(reader.GetString(), CultureInfo.CurrentCulture, DateTimeStyles.None, out value)) return value;
                }
                throw new JsonException("Expected a date/time offset value.");
            }

            public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value);
        }
    }
}
