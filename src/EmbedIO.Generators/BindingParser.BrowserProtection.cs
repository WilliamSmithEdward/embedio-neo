using System.Linq;
using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed partial class BindingParser
    {
        private (bool Valid, string? Code) ReadBrowserProtection(INamedTypeSymbol controller, IMethodSymbol action)
        {
            var attributeType = _compilation.GetTypeByMetadataName("EmbedIO.Security.BrowserProtectionAttribute");

            if (attributeType is null) return (true, null);

            var attribute = InheritedAttributes(action).FirstOrDefault(
                a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType)
            );

            for (var type = controller; type is not null && attribute is null; type = type.BaseType)
                attribute = type
                    .GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));

            if (attribute is null) return (true, null);

            var mode = attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is int value
                ? value
                : 0;

            if (mode < 0 || mode > 1)
            {
                Report(DiagnosticDescriptors.InvalidBrowserProtection, Location(attribute), action.ToDisplayString());
                return (false, null);
            }

            return (true, "(global::EmbedIO.Security.BrowserProtectionMode)" + Literals.Int(mode));
        }
    }
}
