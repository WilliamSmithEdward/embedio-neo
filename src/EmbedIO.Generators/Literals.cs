using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EmbedIO.Generators
{
    // C# source text for type names and constant values in generated code.
    internal static class Literals
    {
        internal static string TypeName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        internal static string String(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

        internal static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        internal static string Identifier(string name)
            => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        // A constant of the given type, as an expression of exactly that type. Null means default.
        internal static string Constant(object? value, ITypeSymbol type)
        {
            var typeName = TypeName(type);
            if (value is null)
                return "default(" + typeName + ")";

            var literal = value switch
            {
                string text => String(text),
                char character => SymbolDisplay.FormatLiteral(character, quote: true),
                bool boolean => boolean ? "true" : "false",
                float single => Single(single),
                double number => Double(number),
                decimal number => number.ToString(CultureInfo.InvariantCulture) + "M",
                long number => number.ToString(CultureInfo.InvariantCulture) + "L",
                ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
                uint number => number.ToString(CultureInfo.InvariantCulture) + "U",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                _ => null,
            };

            return literal is null ? "default(" + typeName + ")" : "(" + typeName + ")(" + literal + ")";
        }

        // A typed constant from attribute data, or null if it cannot be expressed.
        internal static string? TypedConstant(TypedConstant constant)
        {
            switch (constant.Kind)
            {
                case TypedConstantKind.Array:
                    if (constant.Type is not IArrayTypeSymbol arrayType)
                        return null;

                    if (constant.IsNull)
                        return "(" + TypeName(arrayType) + ")null";

                    var elements = constant.Values.Select(TypedConstant).ToArray();
                    return elements.Any(e => e is null)
                        ? null
                        : "new " + TypeName(arrayType.ElementType) + "[] { " + string.Join(", ", elements) + " }";

                case TypedConstantKind.Type:
                    if (constant.Value is null)
                        return "(global::System.Type)null";

                    return constant.Value is INamedTypeSymbol named && !named.IsUnboundGenericType
                        ? "typeof(" + TypeName(named) + ")"
                        : constant.Value is IArrayTypeSymbol array ? "typeof(" + TypeName(array) + ")" : null;

                case TypedConstantKind.Enum:
                case TypedConstantKind.Primitive:
                    if (constant.Type is null)
                        return null;

                    return constant.Value is null
                        ? "(" + TypeName(constant.Type) + ")null"
                        : Constant(constant.Value, constant.Type);

                default:
                    return null;
            }
        }

        private static string Single(float value)
        {
            if (float.IsNaN(value)) return "float.NaN";
            if (float.IsPositiveInfinity(value)) return "float.PositiveInfinity";
            if (float.IsNegativeInfinity(value)) return "float.NegativeInfinity";
            return value.ToString("R", CultureInfo.InvariantCulture) + "F";
        }

        private static string Double(double value)
        {
            if (double.IsNaN(value)) return "double.NaN";
            if (double.IsPositiveInfinity(value)) return "double.PositiveInfinity";
            if (double.IsNegativeInfinity(value)) return "double.NegativeInfinity";
            return value.ToString("R", CultureInfo.InvariantCulture) + "D";
        }

        internal static string HintName(INamedTypeSymbol type)
        {
            var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' ? c : '_').ToArray();
            return new string(chars) + ".ControllerBindings.g.cs";
        }
    }
}
