using System.Linq;
using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed partial class BindingParser
    {
        private (bool Valid, string? Name) ReadSecurityHeaders(INamedTypeSymbol controller, IMethodSymbol action)
        {
            var type = _compilation.GetTypeByMetadataName("EmbedIO.Security.SecurityHeadersAttribute");

            if (type is null) return (true, null);

            var attribute = InheritedAttributes(action).FirstOrDefault(
                a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, type)
            );

            for (var current = controller; current is not null && attribute is null; current = current.BaseType)
                attribute = current
                    .GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, type));

            if (attribute is null) return (true, null);

            if (
                attribute.ConstructorArguments.FirstOrDefault().Value is not string name
                || string.IsNullOrWhiteSpace(name)
                || name.Length > 128
                || name.Any(c => c < 33 || c > 126)
            )
            {
                Report(DiagnosticDescriptors.InvalidSecurityHeaders, Location(attribute), action.ToDisplayString());
                return (false, null);
            }

            return (true, name);
        }
    }
}
