using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EmbedIO.Serialization
{
    /// <summary>JSON serialization using .NET, with dictionary/list values for untyped data.</summary>
    public static class Json
    {
        private static readonly JsonSerializerOptions DefaultOptions = CreateOptions();
        private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(true);

        /// <summary>Creates independent options with EmbedIO's defaults.</summary>
        /// <param name="writeIndented">Whether to indent output.</param>
        /// <returns>Mutable serializer options.</returns>
        public static JsonSerializerOptions CreateOptions(bool writeIndented = false)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(IncludeLegacySetters);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                IncludeFields = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
                AllowTrailingCommas = true,
                WriteIndented = writeIndented,
                TypeInfoResolver = resolver,
            };
            options.Converters.Add(new CompatibleJsonConverterFactory());
            options.Converters.Add(new UntypedValueConverter());
            options.Converters.Add(new PrimitiveStringConverter());
            return options;
        }

        /// <summary>Serializes data with the default options.</summary>
        public static string Serialize(object? data, bool format = false)
            => JsonSerializer.Serialize(data, format ? IndentedOptions : DefaultOptions);

        /// <summary>Serializes data with explicitly supplied options.</summary>
        public static string Serialize(object? data, JsonSerializerOptions options)
            => JsonSerializer.Serialize(data, options ?? throw new ArgumentNullException(nameof(options)));

        /// <summary>Deserializes untyped JSON to dictionaries, lists, and primitive values.</summary>
        public static object? Deserialize(string json) => Deserialize<object>(json);

        /// <summary>Deserializes a JSON value with the default or supplied options.</summary>
        public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
        {
            options ??= DefaultOptions;
            if (UsesCompatibleInput(options))
            {
                if (string.IsNullOrWhiteSpace(json)) return default;
                json = CompatibleJsonInput.Normalize(json, options.ReadCommentHandling);
            }
            return JsonSerializer.Deserialize<T>(json, options);
        }

        /// <summary>Deserializes a JSON value to the specified type.</summary>
        public static object? Deserialize(string json, Type type, JsonSerializerOptions? options = null)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            options ??= DefaultOptions;
            if (UsesCompatibleInput(options))
            {
                if (string.IsNullOrWhiteSpace(json)) return type.IsValueType ? Array.CreateInstance(type, 1).GetValue(0) : null;
                json = CompatibleJsonInput.Normalize(json, options.ReadCommentHandling);
            }
            return JsonSerializer.Deserialize(json, type, options);
        }

        private static void IncludeLegacySetters(JsonTypeInfo typeInfo)
        {
            foreach (var property in typeInfo.Properties)
            {
                if (property.Set != null || !(property.AttributeProvider is PropertyInfo member)
                    || member.GetMethod?.IsPublic != true || member.SetMethod == null || member.SetMethod.IsPublic) continue;
                var ignored = member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition;
                if (ignored == JsonIgnoreCondition.Always || ignored == JsonIgnoreCondition.WhenReading) continue;
                property.Set = member.SetValue;
            }
        }

        private static bool UsesCompatibleInput(JsonSerializerOptions options)
        {
            foreach (var converter in options.Converters)
                if (converter is CompatibleJsonConverterFactory) return true;
            return false;
        }

        private sealed class UntypedValueConverter : JsonConverter<object>
        {
            public override object? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        var dictionary = new Dictionary<string, object?>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            var name = reader.GetString() ?? throw new JsonException("Expected a property name.");
                            if (!reader.Read()) throw new JsonException();
                            dictionary[name] = JsonSerializer.Deserialize<object>(ref reader, options);
                        }
                        return dictionary;
                    case JsonTokenType.StartArray:
                        var list = new List<object?>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            list.Add(JsonSerializer.Deserialize<object>(ref reader, options));
                        return list;
                    case JsonTokenType.String: return reader.GetString();
                    case JsonTokenType.Number:
                        if (reader.TryGetDecimal(out var number)) return number;
                        throw new JsonException("Number exceeds decimal range.");
                    case JsonTokenType.True: return true;
                    case JsonTokenType.False: return false;
                    case JsonTokenType.Null: return null;
                    default: throw new JsonException();
                }
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                if (value.GetType() == typeof(object))
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                else
                    JsonSerializer.Serialize(writer, value, value.GetType(), options);
            }
        }

        private sealed class PrimitiveStringConverter : JsonConverter<string>
        {
            public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String) return reader.GetString();
                if (reader.TokenType == JsonTokenType.Number || reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False)
                {
                    using var value = JsonDocument.ParseValue(ref reader);
                    return value.RootElement.GetRawText();
                }
                throw new JsonException("Expected a string or scalar value.");
            }

            public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
        }
    }
}
