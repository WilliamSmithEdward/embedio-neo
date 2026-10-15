using System;
using System.ComponentModel;
using System.Globalization;

namespace EmbedIO.Internal
{
    // String-to-value conversion shared by reflection binding (FromString) and generated
    // controller binding. The caller supplies the type converter: reflection looks it up
    // with TypeDescriptor, generated code constructs the same converter class directly.
    internal static class StringConversion
    {
        internal static bool TryConvert<TState>(
            Type type,
            string? value,
            TState state,
            Func<TState, Type, TypeConverter> getConverter,
            out object? result,
            out bool invalidValue)
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
                    var converter = getConverter(state, type);
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
    }
}
