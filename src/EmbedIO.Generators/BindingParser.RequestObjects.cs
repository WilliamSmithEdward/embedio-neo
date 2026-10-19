using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed partial class BindingParser
    {
        private const string RequestBindingNamespace = "global::EmbedIO.WebApi.";
        private int _requestBindingCount;
        private List<(IPropertySymbol Property, ParameterMeta Meta)>? _requestObjectFields;

        private static bool IsExplicitRequestBinding(AttributeData a) =>
            a.AttributeClass?.ToDisplayString() is "EmbedIO.WebApi.QueryObjectAttribute"
                or "EmbedIO.WebApi.FormObjectAttribute"
                or "EmbedIO.WebApi.HeaderFieldAttribute"
                or "EmbedIO.WebApi.CookieFieldAttribute"
                or "EmbedIO.WebApi.RouteFieldAttribute";

        private string? BindExplicitRequest(
            IParameterSymbol parameter,
            List<AttributeData> attributes,
            ref bool awaits,
            out string? problem
        )
        {
            problem = null;
            var bindings = attributes
                .Where(IsExplicitRequestBinding)
                .ToArray();

            if (
                bindings.Length != 1
                || RequestDataInterfaces(attributes, _types.RequestDataOfController).Count > 0
                || RequestDataInterfaces(attributes, _types.RequestDataOfControllerAndData).Count > 0
            )
            {
                problem = "a typed request binding cannot be combined with another binding attribute";
                return null;
            }

            var attribute = bindings[0];
            var kind = attribute.AttributeClass?.Name;

            if (
                kind == "FormObjectAttribute"
                && parameter.ContainingSymbol is IMethodSymbol action
                && action.Parameters.Any(
                    p => InheritedParameterAttributes(action, p.Ordinal).Any(
                        a => a.AttributeClass?.ToDisplayString() is "EmbedIO.WebApi.FormFieldAttribute"
                            or "EmbedIO.WebApi.FormDataAttribute"
                            or "EmbedIO.WebApi.JsonDataAttribute"
                            or "EmbedIO.WebApi.MultipartFormAttribute"
                    )
                )
            )
            {
                problem =
                    "FormObject cannot share a body with a legacy form, JSON or multipart binding; use one form DTO";
                return null;
            }

            if (kind == "RouteFieldAttribute" && parameter.Type is IArrayTypeSymbol)
            {
                problem = "RouteField requires a scalar type";
                return null;
            }

            var field = "__requestBinding" + Literals.Int(_requestBindingCount++);
            var type = parameter.Type;
            var typeName = Literals.TypeName(type);

            if (kind is "QueryObjectAttribute" or "FormObjectAttribute")
            {
                if (
                    type is not INamedTypeSymbol model
                    || model.TypeKind != TypeKind.Class
                    || model.IsAbstract
                    || !model.InstanceConstructors.Any(
                        c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public
                    )
                )
                {
                    problem = "a request DTO needs a concrete class and public parameterless constructor";
                    return null;
                }

                var properties = new List<IPropertySymbol>();

                for (var current = model; current != null; current = current.BaseType)
                    foreach (var property in current
                        .GetMembers()
                        .OfType<IPropertySymbol>()
                        .Where(p => !p.IsStatic && p.SetMethod?.DeclaredAccessibility == Accessibility.Public))
                        if (
                            properties.Any(
                                p => p.Name == property.Name
                                    && SymbolEqualityComparer.Default.Equals(
                                        RequestPropertyRoot(p),
                                        RequestPropertyRoot(property)
                                    )
                            )
                        ) continue;

                        else properties.Add(property);

                var ordered = properties
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();

                if (
                    ordered.Length == 0
                    || ordered.Length > 128
                    || ordered.Any(
                        p => p.IsIndexer || p.SetMethod?.IsInitOnly == true || p.GetMethod?.DeclaredAccessibility
                            != Accessibility.Public
                            || p.IsRequired
                    )
                )
                {
                    problem =
                        "request DTOs need 1–128 non-indexed public readable/writable properties; required/init-only members are unsupported";
                    return null;
                }

                _requestObjectFields = [];
                var members = new List<string>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var property in ordered)
                {
                    var options = ValidationAttributes(property).FirstOrDefault(
                        a => a.AttributeClass?.ToDisplayString() == "EmbedIO.WebApi.RequestFieldAttribute"
                    );
                    var name = options?.ConstructorArguments.Length == 1
                        ? options.ConstructorArguments[0].Value as string
                        : property.Name;

                    if (!ValidRequestName(name, false) || !names.Add(name ?? string.Empty))
                    {
                        problem = "request field names must be bounded, valid Unicode and unique ignoring case";
                        return null;
                    }

                    if (!RequestParser(property.Type, out var parser, out problem)) return null;

                    var required = RequestOption(options, "Required", false);
                    var duplicates = RequestOption(options, "DuplicateValues", 0);

                    if (duplicates < 0 || duplicates > 2)
                    {
                        problem = "unknown scalar duplicate handling";
                        return null;
                    }

                    members.Add(
                        RequestBindingNamespace
                            + "RequestObjectMember<"
                            + typeName
                            + ">.Create<"
                            + Literals.TypeName(property.Type)
                            + ">("
                            + Literals.String(name ?? property.Name)
                            + ", "
                            + Literals.String(property.Name)
                            + ", (target, value) => target.@"
                            + property.Name
                            + " = value, "
                            + parser
                            + ", "
                            + Bool(required)
                            + ", "
                            + Bool(property.Type is IArrayTypeSymbol)
                            + ", ("
                            + RequestBindingNamespace
                            + "RequestDuplicateValues)"
                            + Literals.Int(duplicates)
                            + ")"
                    );
                    _requestObjectFields.Add(
                        (
                            property,
                            new ParameterMeta(
                                kind == "FormObjectAttribute"
                                    ? ParameterSource.FormObjectField
                                    : ParameterSource.QueryObjectField,
                                Literals.String(name ?? property.Name),
                                required,
                                null
                            )
                        )
                    );
                }

                _fields
                    .Append("        private static readonly ")
                    .Append(RequestBindingNamespace)
                    .Append("RequestObjectPlan<")
                    .Append(typeName)
                    .Append("> ")
                    .Append(field)
                    .Append(" = new ")
                    .Append(RequestBindingNamespace)
                    .Append("RequestObjectPlan<")
                    .Append(typeName)
                    .Append(">(() => new ")
                    .Append(typeName)
                    .Append("(), new ")
                    .Append(RequestBindingNamespace)
                    .Append("RequestObjectMember<")
                    .Append(typeName)
                    .Append(">[] { ")
                    .Append(string.Join(", ", members))
                    .Append(" }, ")
                    .Append(Bool(RequestOption(attribute, "RejectUnknownFields", false)))
                    .Append(");\n\n");
                var call = RequestBindingNamespace
                    + "RequestBindingServices."
                    + (kind == "FormObjectAttribute" ? "FormAsync" : "Query")
                    + "(__controller, "
                    + field
                    + ", "
                    + Literals.String(parameter.Name)
                    + ")";

                if (kind == "FormObjectAttribute")
                {
                    awaits = true;
                    return "await " + call + ".ConfigureAwait(false)";
                }

                return call;
            }

            var fieldName = attribute.ConstructorArguments.Length == 1
                ? attribute.ConstructorArguments[0].Value as string
                : parameter.Name;

            if (!ValidRequestName(fieldName, kind != "RouteFieldAttribute"))
            {
                problem = "header/cookie names must be bounded ASCII tokens";
                return null;
            }

            if (!RequestParser(type, out var fieldParser, out problem)) return null;

            var duplicateValues = RequestOption(attribute, "DuplicateValues", 0);

            if (duplicateValues < 0 || duplicateValues > 2)
            {
                problem = "unknown scalar duplicate handling";
                return null;
            }

            var isRequired = RequestOption(attribute, "Required", kind == "RouteFieldAttribute");
            _meta = new ParameterMeta(
                kind == "RouteFieldAttribute"
                    ? ParameterSource.RouteOrDefault
                    : kind == "CookieFieldAttribute" ? ParameterSource.Cookie : ParameterSource.Header,
                Literals.String(fieldName ?? parameter.Name),
                isRequired,
                null
            );
            var missing = parameter.HasExplicitDefaultValue
                ? Literals.Constant(parameter.ExplicitDefaultValue, type)
                : "default(" + typeName + ")";
            _fields
                .Append("        private static readonly ")
                .Append(RequestBindingNamespace)
                .Append("RequestFieldPlan<")
                .Append(typeName)
                .Append("> ")
                .Append(field)
                .Append(" = new ")
                .Append(RequestBindingNamespace)
                .Append("RequestFieldPlan<")
                .Append(typeName)
                .Append(">(")
                .Append(Literals.String(fieldName ?? parameter.Name))
                .Append(", ")
                .Append(Bool(kind == "CookieFieldAttribute"))
                .Append(", ")
                .Append(Bool(isRequired))
                .Append(", ")
                .Append(Bool(type is IArrayTypeSymbol))
                .Append(", (")
                .Append(RequestBindingNamespace)
                .Append("RequestDuplicateValues)")
                .Append(Literals.Int(duplicateValues))
                .Append(", ")
                .Append(fieldParser)
                .Append(", () => ")
                .Append(missing)
                .Append(");\n\n");
            return field
                + (kind == "RouteFieldAttribute" ? ".BindRoute(__route, " : ".Bind(__controller, ")
                + Literals.String(parameter.Name)
                + ")";
        }

        private static IPropertySymbol RequestPropertyRoot(IPropertySymbol property)
        {
            while (property.OverriddenProperty != null) property = property.OverriddenProperty;

            return property;
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static T RequestOption<T>(AttributeData? attribute, string name, T fallback)
        {
            if (attribute == null) return fallback;

            foreach (var option in attribute.NamedArguments)
                if (option.Key == name && option.Value.Value is T value) return value;

            return fallback;
        }

        private static bool ValidRequestName(string? name, bool token)
        {
            if (name == null || string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
                return false;

            try
            {
                new UTF8Encoding(false, true).GetByteCount(name);
            }

            catch (EncoderFallbackException)
            {
                return false;
            }

            return !token || name.All(c => c <= 126 && (char.IsLetterOrDigit(c) || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0));
        }

        private bool RequestParser(ITypeSymbol type, out string parser, out string? problem)
        {
            parser = string.Empty;
            problem = null;

            if (type is IArrayTypeSymbol array)
            {
                if (!array.IsSZArray || !RequestScalar(array.ElementType, "value", out var element, out problem))
                {
                    problem ??= "only single-dimensional arrays of supported scalar values can be bound";
                    return false;
                }

                parser = "raw => "
                    + RequestBindingNamespace
                    + "RequestBindingServices.Array<"
                    + Literals.TypeName(array.ElementType)
                    + ">(raw, value => "
                    + element
                    + ")";
                return true;
            }

            if (!RequestScalar(type, "raw[0]", out var scalar, out problem)) return false;

            parser = "raw => " + scalar;
            return true;
        }

        private bool RequestScalar(ITypeSymbol type, string value, out string expression, out string? problem)
        {
            expression = string.Empty;
            problem = null;

            if (type.SpecialType == SpecialType.System_String)
            {
                expression = value;
                return true;
            }

            if (
                type is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            )
            {
                if (!RequestScalar(nullable.TypeArguments[0], value, out var inner, out problem)) return false;

                expression = "(global::System.String.IsNullOrEmpty("
                    + value
                    + ") ? default("
                    + Literals.TypeName(type)
                    + ") : ("
                    + Literals.TypeName(type)
                    + ")("
                    + inner
                    + "))";
                return true;
            }

            if (TryGetConverter(type, out var converter))
            {
                expression = RequestBindingNamespace
                    + "RequestBindingServices.Parse<"
                    + Literals.TypeName(type)
                    + ">("
                    + value
                    + ", "
                    + converter
                    + ")";
                return true;
            }

            if (type is INamedTypeSymbol named)
            {
                var methods = named
                    .GetMembers("TryParse")
                    .OfType<IMethodSymbol>()
                    .Where(
                        m => m.IsStatic
                            && !m.IsGenericMethod
                            && m.DeclaredAccessibility == Accessibility.Public
                            && m.ReturnType.SpecialType == SpecialType.System_Boolean
                    )
                    .OrderByDescending(m => m.Parameters.Length);

                foreach (var method in methods)
                {
                    var args = method.Parameters;

                    if (
                        (args.Length == 2
                            || args.Length == 3 && args[1].Type.ToDisplayString() == "System.IFormatProvider")
                        && args[0].Type.SpecialType == SpecialType.System_String
                        && args[args.Length - 1].RefKind == RefKind.Out
                        && SymbolEqualityComparer.Default.Equals(args[args.Length - 1].Type, type)
                    )
                    {
                        expression = "("
                            + Literals.TypeName(type)
                            + ".TryParse("
                            + value
                            + (args.Length == 3
                                ? ", global::System.Globalization.CultureInfo.InvariantCulture"
                                : string.Empty)
                            + ", out var parsed) ? "
                            + (type.IsReferenceType
                                ? "(parsed ?? throw new global::System.FormatException())"
                                : "parsed")
                            + " : throw new global::System.FormatException())";
                        return true;
                    }
                }
            }

            problem =
                "request fields need an accessible string converter or public TryParse(string, [IFormatProvider,] out T); nested objects are unsupported";
            return false;
        }
    }
}
