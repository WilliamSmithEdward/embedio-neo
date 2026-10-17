using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed partial class BindingParser
    {
        private const string ValidationNamespace = "global::EmbedIO.WebApi.";
        private const string RulesType = "global::System.ComponentModel.DataAnnotations.ValidationAttribute";
        private readonly Dictionary<ITypeSymbol, string> _validationPlans = new(SymbolEqualityComparer.Default);
        private int _validationParameters;
        private int _validationPlanCount;

        private static bool IsValidationRule(AttributeData attribute)
        {
            for (var type = attribute.AttributeClass; type != null; type = type.BaseType)
                if (type.ToDisplayString() == "System.ComponentModel.DataAnnotations.ValidationAttribute") return true;
            return false;
        }

        private string ValidationRules(IEnumerable<AttributeData> attributes)
        {
            var expressions = new List<string>();
            foreach (var attribute in attributes.Where(IsValidationRule))
            {
                var attributeName = attribute.AttributeClass?.ToDisplayString();
                if (attributeName == "System.ComponentModel.DataAnnotations.CompareAttribute"
                    || attributeName == "System.ComponentModel.DataAnnotations.CustomValidationAttribute"
                    || attributeName == "System.ComponentModel.DataAnnotations.RangeAttribute" && attribute.ConstructorArguments.Length == 3
                    || attribute.NamedArguments.Any(option => option.Key == "ErrorMessageResourceType"))
                {
                    Report(DiagnosticDescriptors.InvalidValidation, Location(attribute), "reflection-dependent rules and resource lookup need an AOT-safe custom ValidationAttribute or IValidatableObject rule");
                    continue;
                }
                var expression = AttributeExpression(attribute);
                if (expression == null || attribute.AttributeClass == null || !_compilation.IsSymbolAccessibleWithin(attribute.AttributeClass, _context))
                    Report(DiagnosticDescriptors.InvalidValidation, Location(attribute), "validation attributes must be constructible and accessible from the generated binding");
                else expressions.Add(expression);
            }
            return "new " + RulesType + "[] { " + string.Join(", ", expressions) + " }";
        }

        private string? ValidationParameter(IParameterSymbol parameter)
        {
            var attribute = parameter.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "EmbedIO.WebApi.ValidateAttribute");
            if (attribute == null) return null;
            foreach (var option in attribute.NamedArguments)
                if (option.Value.Value is int limit && (limit < 1 || limit > (option.Key == "MaxDepth" ? 64 : option.Key == "MaxNodes" ? 100000 : 1024)))
                    Report(DiagnosticDescriptors.InvalidValidation, Location(attribute), "validation limits must be positive: depth <= 64, nodes <= 100000, errors <= 1024");
            _validationPlans.Clear();
            var plan = ValidationPlan(parameter.Type);
            var name = "__validation" + Literals.Int(_validationParameters++);
            _fields.Append("        private static readonly ").Append(ValidationNamespace).Append("RequestValidationParameter ").Append(name)
                .Append(" = new ").Append(ValidationNamespace).Append("RequestValidationParameter(").Append(plan).Append(", ")
                .Append(ValidationRules(parameter.GetAttributes())).Append(", ").Append(AttributeExpression(attribute)).Append(");\n\n");
            return name;
        }

        private static List<AttributeData> ValidationAttributes(ISymbol symbol)
        {
            var result = new List<AttributeData>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var inherited = false;
            for (ISymbol? current = symbol; current != null; current = current is IPropertySymbol property ? property.OverriddenProperty : (current as INamedTypeSymbol)?.BaseType)
            {
                foreach (var attribute in current.GetAttributes())
                {
                    var usage = attribute.AttributeClass?.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.AttributeUsageAttribute");
                    if (inherited && usage?.NamedArguments.Any(a => a.Key == "Inherited" && a.Value.Value is false) == true) continue;
                    var multiple = usage?.NamedArguments.Any(a => a.Key == "AllowMultiple" && a.Value.Value is true) == true;
                    if (multiple || seen.Add(attribute.AttributeClass?.ToDisplayString() ?? string.Empty)) result.Add(attribute);
                }
                inherited = true;
            }
            return result;
        }

        private string ValidationPlan(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) type = nullable.TypeArguments[0];
            if (_validationPlans.TryGetValue(type, out var found)) return found;
            var name = "__validationPlan" + Literals.Int(_validationPlanCount++);
            _validationPlans.Add(type, name);
            var members = new List<string>();
            if (_validationPlans.Count > 128)
            {
                Report(DiagnosticDescriptors.InvalidValidation, type.Locations.FirstOrDefault(), "validation model graphs may contain at most 128 declared types");
                _fields.Append("        private static readonly ").Append(ValidationNamespace).Append("RequestValidationPlan ").Append(name).Append(" = null!;\n");
                return name;
            }
            string? element = null;
            var sequence = type is INamedTypeSymbol named ? named.AllInterfaces.Concat(new[] { named })
                .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>") : null;
            var scalar = type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum
                || new[] { "System.DateTime", "System.DateTimeOffset", "System.TimeSpan", "System.Guid", "System.Uri", "System.DateOnly", "System.TimeOnly" }.Contains(type.ToDisplayString());
            if (type is IArrayTypeSymbol array) element = ValidationPlan(array.ElementType);
            else if (sequence != null && type.SpecialType != SpecialType.System_String) element = ValidationPlan(sequence.TypeArguments[0]);
            else if (!scalar && type is INamedTypeSymbol model)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var current = model; current != null; current = current.BaseType)
                {
                    foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                    {
                        if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public || !seen.Add(property.Name)) continue;
                        if (property.GetMethod?.DeclaredAccessibility != Accessibility.Public) continue;
                        if (!_compilation.IsSymbolAccessibleWithin(property, _context))
                        {
                            Report(DiagnosticDescriptors.InvalidValidation, property.Locations.FirstOrDefault(), "validated properties must be accessible from the generated binding");
                            continue;
                        }
                        var attributes = ValidationAttributes(property);
                        var ignore = attributes.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute");
                        if (ignore != null && (ignore.NamedArguments.Length == 0 || ignore.NamedArguments.Any(a => a.Key == "Condition" && a.Value.Value is int condition && condition == 1))) continue;
                        var jsonName = attributes.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute")?.ConstructorArguments.FirstOrDefault().Value as string;
                        var child = ValidationPlan(property.Type);
                        members.Add("new " + ValidationNamespace + "RequestValidationMember(" + Literals.String(property.Name) + ", "
                            + (jsonName == null ? "null" : Literals.String(jsonName)) + ", __value => ((" + Literals.TypeName(property.ContainingType) + ")__value)." + Literals.Identifier(property.Name)
                            + ", " + ValidationRules(attributes) + ", () => " + child + ")");
                    }
                }
            }
            if (type is INamedTypeSymbol untyped && element == null && type.SpecialType != SpecialType.System_String
                && untyped.AllInterfaces.Concat(new[] { untyped }).Any(i => i.ToDisplayString() == "System.Collections.IEnumerable"))
                Report(DiagnosticDescriptors.InvalidValidation, type.Locations.FirstOrDefault(), "untyped collections cannot be validated; use IEnumerable<T>");
            if (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Interface && element == null
                || type is INamedTypeSymbol dictionary && dictionary.AllInterfaces.Concat(new[] { dictionary }).Any(i => (i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IDictionary<TKey, TValue>" || i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>")))
                Report(DiagnosticDescriptors.InvalidValidation, type.Locations.FirstOrDefault(), "validation requires concrete models or typed sequences; dictionaries and untyped values are unsupported");
            _fields.Append("        private static readonly ").Append(ValidationNamespace).Append("RequestValidationPlan ").Append(name)
                .Append(" = new ").Append(ValidationNamespace).Append("RequestValidationPlan(typeof(").Append(Literals.TypeName(type)).Append("), ")
                .Append(ValidationRules(ValidationAttributes(type))).Append(", new ").Append(ValidationNamespace).Append("RequestValidationMember[] { ")
                .Append(string.Join(", ", members)).Append(" }").Append(element == null ? string.Empty : ", () => " + element).Append(");\n\n");
            return name;
        }
    }
}
