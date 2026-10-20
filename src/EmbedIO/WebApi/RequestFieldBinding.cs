using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.WebApi
{
    // Query and form field binding shared by QueryFieldAttribute, FormFieldAttribute and
    // generated controller binding. Typed conversions take the converter that
    // TypeDescriptor returns for the target type; generated code constructs it directly.
    internal static class RequestFieldBinding
    {
        internal const string Query = "query";
        internal const string Form = "form";

        internal static string? GetString(
            NameValueCollection data,
            string fieldName,
            bool badRequestIfMissing,
            string kind
        )
        {
            EnsurePresent(data, fieldName, badRequestIfMissing, kind);
            return data
                .GetValues(fieldName)
                ?.LastOrDefault();
        }

        internal static string[] GetStrings(
            NameValueCollection data,
            string fieldName,
            bool badRequestIfMissing,
            string kind
        )
        {
            EnsurePresent(data, fieldName, badRequestIfMissing, kind);
            return data.GetValues(fieldName) ?? [];
        }

        internal static T? Get<T>(
            NameValueCollection data,
            string fieldName,
            bool badRequestIfMissing,
            string kind,
            string parameterName,
            TypeConverter? converter
        )
        {
            var fieldValue = GetString(data, fieldName, badRequestIfMissing, kind);

            if (fieldValue == null)
                return default;

            if (!TryConvert(typeof(T), fieldValue, converter, out var result))
                throw HttpException.BadRequest($"Cannot convert field {fieldName} to {typeof(T).Name}.");

            return CastResult<T>(parameterName, result);
        }

        internal static TElement?[] GetArray<TElement>(
            NameValueCollection data,
            string fieldName,
            bool badRequestIfMissing,
            string kind,
            TypeConverter? converter
        )
        {
            var fieldValues = GetStrings(data, fieldName, badRequestIfMissing, kind);
            var array = new TElement?[fieldValues.Length];

            for (var i = 0; i < fieldValues.Length; i++)
            {
                if (!TryConvert(typeof(TElement), fieldValues[i], converter, out var element))
                    throw HttpException.BadRequest(
                        $"Cannot convert field {fieldName} to an array of {typeof(TElement).Name}."
                    );

                // Array.SetValue semantics: null stores the default value, another type is an invalid cast.
                array[i] = element switch
                {
                    null => default,
                    TElement typed => typed,
                    _ =>
                        throw new InvalidCastException(
                            $"Cannot cast {element.GetType().FullName} to {typeof(TElement).FullName}."
                        )
                };
            }

            return array;
        }

        // The IRequestDataAttribute<TController> path of QueryFieldAttribute and FormFieldAttribute,
        // used by the reflection-based binder, which converts to a type known only at run time.
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        internal static object? GetUntyped(
            NameValueCollection data,
            string fieldName,
            bool badRequestIfMissing,
            string kind,
            Type type
        )
        {
            EnsurePresent(data, fieldName, badRequestIfMissing, kind);

            if (type.IsArray)
            {
                var fieldValues = data.GetValues(fieldName) ?? [];

                if (!FromString.TryConvertTo(type, fieldValues, out var arrayResult))
                    throw HttpException.BadRequest(
                        $"Cannot convert field {fieldName} to an array of {type.GetElementType()?.Name}."
                    );

                return arrayResult;
            }

            var fieldValue = data
                .GetValues(fieldName)
                ?.LastOrDefault();

            if (fieldValue == null)
                return type.IsValueType ? Activator.CreateInstance(type) : null;

            if (!FromString.TryConvertTo(type, fieldValue, out var result))
                throw HttpException.BadRequest($"Cannot convert field {fieldName} to {type.Name}.");

            return result;
        }

        // The cast the reflection binder applies to IRequestDataAttribute<TController> results.
        internal static T? CastResult<T>(string parameterName, object? result) => result switch
        {
            null when typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null =>
                throw new InvalidCastException(
                    $"Cannot cast null to {typeof(T).FullName} for parameter \"{parameterName}\"."
                ),
            null => default,
            T castResult => castResult,
            _ =>
                throw new InvalidCastException(
                    $"Cannot cast {result.GetType().FullName} to {typeof(T).FullName} for parameter \"{parameterName}\"."
                )
        };

        internal static bool TryConvert(Type type, string? value, TypeConverter? converter, out object? result) =>
            StringConversion.TryConvert(type, value, converter, ConverterOrThrow, out result, out _);

        internal static TypeConverter ConverterOrThrow(TypeConverter? converter, Type type) =>
            converter ?? throw new InvalidOperationException($"No type converter was supplied for {type.FullName}.");

        private static void EnsurePresent(
            NameValueCollection data,
            string fieldName,
            bool badRequestIfMissing,
            string kind
        )
        {
            // Presence follows the collection's own key comparison, which binding uses to read the value.
            if (badRequestIfMissing && data.GetValues(fieldName) == null && !data.ContainsKey(fieldName))
                throw HttpException.BadRequest($"Missing {kind} field {fieldName}.");
        }
    }
}
