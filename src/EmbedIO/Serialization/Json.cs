using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace EmbedIO.Serialization
{
    /// <summary>JSON serialization using .NET, with dictionary/list values for untyped data.</summary>
    public static class Json
    {
        private static JsonSerializerOptions? _defaultOptions;
        private static JsonSerializerOptions? _indentedOptions;

        // Created on first use: reflection-based options are unavailable to trimmed and AOT applications.
        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
        internal static JsonSerializerOptions DefaultOptions() =>
            Volatile.Read(ref _defaultOptions)
                ?? Interlocked.CompareExchange(ref _defaultOptions, CreateOptions(), null)
                ?? _defaultOptions;

        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
        private static JsonSerializerOptions IndentedOptions() =>
            Volatile.Read(ref _indentedOptions)
                ?? Interlocked.CompareExchange(ref _indentedOptions, CreateOptions(true), null)
                ?? _indentedOptions;

        /// <summary>Creates independent options with EmbedIO's defaults.</summary>
        /// <param name="writeIndented">Whether to indent output.</param>
        /// <returns>Mutable serializer options.</returns>
        /// <remarks>The options resolve type metadata by reflection, which trimmed and Native AOT
        /// applications cannot use; they supply source-generated metadata instead.</remarks>
        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
        public static JsonSerializerOptions CreateOptions(bool writeIndented = false)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(IncludeLegacySetters);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                IncludeFields = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
                    | JsonNumberHandling.AllowNamedFloatingPointLiterals,
                AllowTrailingCommas = true,
                WriteIndented = writeIndented,
                TypeInfoResolver = resolver
            };
            options.Converters.Add(new CompatibleJsonConverterFactory());
            options.Converters.Add(new UntypedValueConverter());
            options.Converters.Add(new PrimitiveStringConverter());
            return options;
        }

        /// <summary>Serializes data with the default options.</summary>
        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
        public static string Serialize(object? data, bool format = false) =>
            JsonSerializer.Serialize(data, format ? IndentedOptions() : DefaultOptions());

        /// <summary>Serializes data with explicitly supplied options.</summary>
        /// <remarks>Metadata comes from the options' type info resolver, so options whose resolver
        /// is a <see cref="JsonSerializerContext"/> work in trimmed and Native AOT applications.</remarks>
        public static string Serialize(object? data, JsonSerializerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (options.TypeInfoResolver == null) return SerializeByReflection(data, options);

            return data == null ? "null" : JsonSerializer.Serialize(data, options.GetTypeInfo(data.GetType()));
        }

        // Options without a resolver get System.Text.Json's reflection default, as they always have.
        private static string SerializeByReflection(object? data, JsonSerializerOptions options) =>
            ReflectionSerialization.IsSupported
                ? JsonSerializer.Serialize(data, options)
                : throw ReflectionSerialization.Disabled();

        /// <summary>Serializes a value with source-generated or otherwise explicit metadata.</summary>
        /// <typeparam name="T">The value's type.</typeparam>
        /// <param name="data">The value to serialize.</param>
        /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, for example from a
        /// <see cref="JsonSerializerContext"/>. Safe for trimmed and Native AOT applications.</param>
        /// <returns>The JSON text.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is null.</exception>
        public static string SerializeWithMetadata<T>(T data, JsonTypeInfo<T> typeInfo) =>
            JsonSerializer.Serialize(data, typeInfo ?? throw new ArgumentNullException(nameof(typeInfo)));

        // HTTP JSON already promises UTF-8; avoid a complete UTF-16 intermediate
        // and the text writer's many small output writes for a large value. Metadata comes
        // from the options' resolver, so source-generated options need no reflection.
        internal static byte[] SerializeUtf8(object? data, JsonSerializerOptions options)
        {
            if (options.TypeInfoResolver == null)
                return ReflectionSerialization.IsSupported
                    ? JsonSerializer.SerializeToUtf8Bytes(data, options)
                    : throw ReflectionSerialization.Disabled();

            return data == null
                ? NullUtf8()
                : JsonSerializer.SerializeToUtf8Bytes(data, options.GetTypeInfo(data.GetType()));
        }

        private static byte[] NullUtf8() =>
        [
            (byte)'n',
            (byte)'u',
            (byte)'l',
            (byte)'l'
        ];

        // Deserializes through the options' resolver, applying EmbedIO's input compatibility
        // when the options carry it. Source-generated options need no reflection.
        internal static T? DeserializeWith<T>(string json, JsonSerializerOptions options)
        {
            if (UsesCompatibleInput(options))
            {
                if (string.IsNullOrWhiteSpace(json)) return default;

                json = CompatibleJsonInput.Normalize(json, options.ReadCommentHandling);
            }

            if (options.TypeInfoResolver == null)
                return ReflectionSerialization.IsSupported
                    ? JsonSerializer.Deserialize<T>(json, options)
                    : throw ReflectionSerialization.Disabled();

            return (T?)JsonSerializer.Deserialize(json, options.GetTypeInfo(typeof(T)));
        }

        /// <summary>Deserializes untyped JSON to dictionaries, lists, and primitive values.</summary>
        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
        public static object? Deserialize(string json) => Deserialize<object>(json);

        /// <summary>Deserializes a value with source-generated or otherwise explicit metadata.</summary>
        /// <typeparam name="T">The value's type.</typeparam>
        /// <param name="json">The JSON text.</param>
        /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, for example from a
        /// <see cref="JsonSerializerContext"/>. Safe for trimmed and Native AOT applications.</param>
        /// <returns>The deserialized value.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> or <paramref name="typeInfo"/> is null.</exception>
        public static T? DeserializeWithMetadata<T>(string json, JsonTypeInfo<T> typeInfo) =>
            JsonSerializer.Deserialize(
                json ?? throw new ArgumentNullException(nameof(json)),
                typeInfo ?? throw new ArgumentNullException(nameof(typeInfo))
            );

        /// <summary>Deserializes a JSON value with the default or supplied options.</summary>
        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
        public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
        {
            options ??= DefaultOptions();

            if (UsesCompatibleInput(options))
            {
                if (string.IsNullOrWhiteSpace(json)) return default;

                json = CompatibleJsonInput.Normalize(json, options.ReadCommentHandling);
            }

            return JsonSerializer.Deserialize<T>(json, options);
        }

        /// <summary>Deserializes a JSON value to the specified type.</summary>
        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
        public static object? Deserialize(string json, Type type, JsonSerializerOptions? options = null)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            options ??= DefaultOptions();

            if (UsesCompatibleInput(options))
            {
                if (string.IsNullOrWhiteSpace(json))
                    return type.IsValueType
                        ? Array
                            .CreateInstance(type, 1)
                            .GetValue(0)
                        : null;

                json = CompatibleJsonInput.Normalize(json, options.ReadCommentHandling);
            }

            return JsonSerializer.Deserialize(json, type, options);
        }

        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
        private static void IncludeLegacySetters(JsonTypeInfo typeInfo)
        {
            foreach (var property in typeInfo.Properties)
            {
                if (
                    property.Set != null
                        || property.AttributeProvider is not PropertyInfo member
                        || member.GetMethod?.IsPublic
                        != true
                    || member.SetMethod == null
                    || member.SetMethod.IsPublic
                ) continue;

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

        [RequiresUnreferencedCode(ReflectionSerialization.Message)]
        [RequiresDynamicCode(ReflectionSerialization.Message)]
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

                    case JsonTokenType.String:
                        return reader.GetString();

                    case JsonTokenType.Number:
                        if (reader.TryGetDecimal(out var number)) return number;

                        throw new JsonException("Number exceeds decimal range.");

                    case JsonTokenType.True:
                        return true;

                    case JsonTokenType.False:
                        return false;

                    case JsonTokenType.Null:
                        return null;

                    default:
                        throw new JsonException();
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

                if (
                    reader.TokenType == JsonTokenType.Number
                    || reader.TokenType == JsonTokenType.True
                    || reader.TokenType == JsonTokenType.False
                )
                {
                    using var value = JsonDocument.ParseValue(ref reader);
                    return value.RootElement.GetRawText();
                }

                throw new JsonException("Expected a string or scalar value.");
            }

            public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
                writer.WriteStringValue(value);
        }
    }
}
