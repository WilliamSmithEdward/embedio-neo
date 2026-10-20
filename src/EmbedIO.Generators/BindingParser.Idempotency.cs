using System.Linq;
using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed partial class BindingParser
    {
        private string? ReadIdempotency(INamedTypeSymbol controller, IMethodSymbol action)
        {
            var attributeType = _compilation.GetTypeByMetadataName("EmbedIO.Idempotency.IdempotencyAttribute");
            var attribute = InheritedAttributes(action).FirstOrDefault(
                a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType)
            );

            for (var type = controller; type is not null && attribute is null; type = type.BaseType)
                attribute = type
                    .GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));

            return attribute?.ConstructorArguments.FirstOrDefault().Value as string;
        }
    }
}
