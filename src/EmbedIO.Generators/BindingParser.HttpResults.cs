using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed partial class BindingParser
    {
        private ITypeSymbol? HttpResultReturnType(ITypeSymbol type)
        {
            var candidate = type;

            if (
                type is INamedTypeSymbol named
                && named.IsGenericType
                && (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _types.TaskOfT)
                    || IsValueTask(type))
            )
                candidate = named.TypeArguments[0];

            for (var current = candidate as INamedTypeSymbol; current != null; current = current.BaseType)
                if (current.Name == "HttpResult" && current.ContainingNamespace.ToDisplayString() == "EmbedIO")
                    return candidate;

            return null;
        }

        private IEnumerable<string> HttpResultResponses(IMethodSymbol method, IEnumerable<AttributeData> attributes)
        {
            var declared = new HashSet<int>(
                attributes
                    .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _types.OpenApiResponse))
                    .Where(a => a.ConstructorArguments.Length == 1)
                    .Select(a => System.Convert.ToInt32(a.ConstructorArguments[0].Value, CultureInfo.InvariantCulture))
            );

            foreach (var (Status, Type) in ResultResponses(HttpResultReturnType(method.ReturnType)))
                if (declared.Add(Status))
                    yield return "new global::EmbedIO.OpenApi.OpenApiResponseAttribute("
                        + Status.ToString(CultureInfo.InvariantCulture)
                        + ") { Type = "
                        + (Type == null ? "null" : TypeOf(Type))
                        + " }";
        }

        private static IEnumerable<(int Status, ITypeSymbol? Type)> ResultResponses(ITypeSymbol? type)
        {
            if (type is not INamedTypeSymbol named || named.ContainingNamespace.ToDisplayString() != "EmbedIO")
                yield break;

            if (named.Name == "HttpResult" && named.TypeArguments.Length is 2 or 3)
            {
                foreach (var item in named.TypeArguments)
                    foreach (var response in ResultResponses(item)) yield return response;

                yield break;
            }

            var status = named.Name switch
            {
                "OkResult" => 200,
                "JsonHttpResult" => 200,
                "CreatedResult" => 201,
                "AcceptedResult" => 202,
                "NotFoundResult" => 404,
                "BadRequestResult" => 400,
                "ConflictResult" => 409,
                "NoContentResult" => 204,
                _ => 0
            };

            if (status != 0)
                yield return (
                    status,
                    named.TypeArguments.Length == 1 && status is >= 200 and < 300 ? named.TypeArguments[0] : null
                );
        }
    }
}
