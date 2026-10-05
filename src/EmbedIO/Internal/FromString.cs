using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;

namespace EmbedIO.Internal
{
    internal static class FromString
    {
        internal static bool TryConvertTo(Type type, string? value, out object? result)
        {
            result = null;
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null && string.IsNullOrEmpty(value)) return true;
            if (value == null) return false;
            type = underlying ?? type;
            if (type == typeof(string)) { result = value; return true; }
            try
            {
                if (type.IsEnum) result = Enum.Parse(type, value, true);
                else
                {
                    var converter = TypeDescriptor.GetConverter(type);
                    if (!converter.CanConvertFrom(typeof(string))) return false;
                    result = converter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
                }
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is FormatException || exception is OverflowException || exception is NotSupportedException)
            {
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
            if (TryConvertTo(type, value, out var result)) return result;
            throw new FormatException($"Cannot convert '{value}' to {type.FullName}.");
        }
    }
}
