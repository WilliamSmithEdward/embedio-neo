using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;

namespace EmbedIO.Internal
{
    internal static class FromString
    {
        internal static bool TryConvertTo(Type type, string? value, out object? result)
            => TryConvertTo(type, value, out result, out _);

        // Distinguish client input from unsupported/missing binding configuration.
        // Query/form callers keep their existing boolean conversion contract.
        private static bool TryConvertTo(Type type, string? value, out object? result, out bool invalidValue)
        {
            result = null;
            invalidValue = false;
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null && string.IsNullOrEmpty(value)) return true;
            if (value == null) return false;
            type = underlying ?? type;
            if (type == typeof(string)) { result = value; return true; }
            try
            {
                if (type.IsEnum)
                {
                    invalidValue = !string.IsNullOrEmpty(value);
                    result = Enum.Parse(type, value, true);
                }
                else
                {
                    var converter = TypeDescriptor.GetConverter(type);
                    if (!converter.CanConvertFrom(typeof(string))) return false;
                    invalidValue = !string.IsNullOrEmpty(value);
                    result = converter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
                }
                invalidValue = false;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is FormatException || exception is OverflowException || exception is NotSupportedException)
            {
                if (exception is NotSupportedException) invalidValue = false;
                return false;
            }
        }

        internal static bool TryConvertTo(Type type, string[] values, out object? result)
        {
            result = null;
            if (!type.IsArray || type.GetArrayRank() != 1) return false;
            var elementType = type.GetElementType()!;
            var array = Array.CreateInstance(elementType, values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                if (!TryConvertTo(elementType, values[i], out var element)) return false;
                array.SetValue(element, i);
            }
            result = array;
            return true;
        }

        internal static Expression ConvertExpressionTo(Type type, Expression value)
            => Expression.Convert(Expression.Call(typeof(FromString), nameof(ConvertTo), null,
                Expression.Constant(type), value), type);

        private static object? ConvertTo(Type type, string? value)
        {
            bool invalidValue;
            try
            {
                if (TryConvertTo(type, value, out var result, out invalidValue)) return result;
            }
            catch (Exception exception) when (!string.IsNullOrEmpty(value)
                && exception.GetType() == typeof(Exception)
                && exception.TargetSite?.DeclaringType == typeof(BaseNumberConverter)
                && exception.TargetSite.Name == nameof(TypeConverter.ConvertFrom)
                && (exception.InnerException is ArgumentException || exception.InnerException is FormatException
                    || exception.InnerException is OverflowException || exception.InnerException is IndexOutOfRangeException))
            {
                // Framework's BCL numeric converter wraps parsing failures in plain
                // Exception. Translate only this verified origin at the route boundary;
                // generic application wrappers and query/form conversion are untouched.
                throw HttpException.BadRequest("Invalid route parameter value.");
            }
            // This expression conversion is used only for controller route arguments.
            // Do not translate controller execution or unsupported converter errors.
            if (invalidValue) throw HttpException.BadRequest("Invalid route parameter value.");
            throw new FormatException($"Cannot convert '{value}' to {type.FullName}.");
        }
    }
}
