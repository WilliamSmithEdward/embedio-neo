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

            foreach (var responses in ResultResponses(HttpResultReturnType(method.ReturnType)).GroupBy(
                response => response.Status
            ))
            {
                if (!declared.Add(responses.Key)) continue;

                var selected = responses.First();

                // A general problem permits validation extensions too. Do not narrow a shared status
                // to a validation-only body when another alternative can send a general problem.
                foreach (var candidate in responses)
                    if (candidate.Problem && (!selected.Problem || selected.Type?.Name == "ValidationProblemResult"))
                        selected = candidate;

                yield return "new global::EmbedIO.OpenApi.OpenApiResponseAttribute("
                    + selected.Status.ToString(CultureInfo.InvariantCulture)
                    + ") { Type = "
                    + (selected.Type == null ? "null" : TypeOf(selected.Type))
                    + (selected.Problem ? ", ContentType = \"application/problem+json\"" : string.Empty)
                    + " }";
            }
        }

        private static bool IsProblemResult(ITypeSymbol? type) =>
            type is INamedTypeSymbol named
                && named.ContainingNamespace.ToDisplayString() == "EmbedIO"
                && named.Name is "BadRequestProblemResult"
                    or "NotFoundProblemResult"
                    or "ConflictProblemResult"
                    or "ValidationProblemResult";

        private static IEnumerable<(int Status, ITypeSymbol? Type, bool Problem)> ResultResponses(ITypeSymbol? type)
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
                "BadRequestProblemResult" => 400,
                "ValidationProblemResult" => 400,
                "NotFoundProblemResult" => 404,
                "ConflictProblemResult" => 409,
                "ConflictResult" => 409,
                "NoContentResult" => 204,
                _ => 0
            };

            if (status != 0)
                yield return (
                    status,
                    IsProblemResult(named)
                        ? named
                        : named.TypeArguments.Length == 1 && status is >= 200 and < 300 ? named.TypeArguments[0] : null,
                    IsProblemResult(named)
                );
        }
    }
}
