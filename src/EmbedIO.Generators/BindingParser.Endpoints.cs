using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed partial class BindingParser
    {
        private IMethodSymbol? _endpointMethod;

        internal static BindingModel? ParseEndpoints(GeneratorAttributeSyntaxContext context, CancellationToken token)
        {
            if (context.TargetSymbol is not INamedTypeSymbol owner) return null;

            var types = WellKnownTypes.Create(context.SemanticModel.Compilation);

            if (types == null) return null;

            var parser = new BindingParser(context.SemanticModel.Compilation, owner, types);
            var attribute = context.Attributes[0];
            var failed = false;

            if (!owner.IsStatic || !IsPartialAndNonGeneric(owner))
            {
                parser.Report(
                    DiagnosticDescriptors.InvalidEndpoint,
                    owner.Locations.FirstOrDefault(),
                    "use a non-generic static partial class (and partial containing classes)"
                );
                failed = true;
            }

            if (!parser.TryGetJsonContext(attribute, Location(attribute), out var jsonContext)) failed = true;

            if (
                owner
                    .GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, types.GeneratedControllerBinding))
            )
            {
                parser.Report(
                    DiagnosticDescriptors.InvalidEndpoint,
                    owner.Locations.FirstOrDefault(),
                    "use separate endpoint and controller binding classes"
                );
                failed = true;
            }

            if (
                owner
                    .GetAttributes()
                    .Any(a => a.AttributeClass?.ToDisplayString() == "EmbedIO.WebApi.BeforeHandlerAttribute")
            )
            {
                parser.Report(
                    DiagnosticDescriptors.InvalidEndpoint,
                    Location(attribute),
                    "configure minimal endpoint hooks through OnBeforeHandler/OnAfterHandler"
                );
                failed = true;
            }

            var methods = owner
                .GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public)
                .ToArray();

            if (methods.Length == 0)
            {
                parser.Report(
                    DiagnosticDescriptors.InvalidEndpoint,
                    Location(attribute),
                    "declare at least one public static handler"
                );
                failed = true;
            }

            foreach (var method in methods)
            {
                token.ThrowIfCancellationRequested();
                var name = method.Name + "Endpoint";

                if (
                    !method.IsStatic
                    || method.IsAsync && method.ReturnsVoid
                    || method.IsGenericMethod
                    || methods.Count(m => m.Name == method.Name) != 1
                    || owner.GetMembers(name).Length != 0
                )
                {
                    parser.Report(
                        DiagnosticDescriptors.InvalidEndpoint,
                        method.Locations.FirstOrDefault(),
                        "handlers must be static, non-generic and uniquely named; generated Endpoint property names must be unused"
                    );
                    failed = true;
                    continue;
                }

                var bodies = method.Parameters
                    .Select(
                        p => p
                            .GetAttributes()
                            .Any(a => a.AttributeClass?.ToDisplayString() == "EmbedIO.WebApi.JsonDataAttribute")
                            ? 1
                            : p
                                .GetAttributes()
                                .Any(
                                    a => a.AttributeClass?.ToDisplayString() == "EmbedIO.WebApi.MultipartFormAttribute"
                                )
                                ? 2
                                : p
                                    .GetAttributes()
                                    .Any(
                                        a => a.AttributeClass?.ToDisplayString() == "EmbedIO.WebApi.FormObjectAttribute"
                                    )
                                    ? 3
                                    : p
                                        .GetAttributes()
                                        .Any(
                                            a => a.AttributeClass?.ToDisplayString(
                                            ) is "EmbedIO.WebApi.FormFieldAttribute"
                                                or "EmbedIO.WebApi.FormDataAttribute"
                                        )
                                        ? 4
                                        : 0
                    )
                    .Where(k => k != 0)
                    .ToArray();

                if (bodies.Length > 1 && bodies.Any(k => k != 4))
                {
                    parser.Report(
                        DiagnosticDescriptors.InvalidEndpoint,
                        method.Locations.FirstOrDefault(),
                        "use a single JSON, multipart or form DTO body binding per endpoint"
                    );
                    failed = true;
                    continue;
                }

                if (!parser.ValidateAuthorization(owner, method))
                {
                    failed = true;
                    continue;
                }

                var security = parser.ReadSecurityHeaders(owner, method);
                var browser = parser.ReadBrowserProtection(owner, method);

                if (!security.Valid || !browser.Valid)
                {
                    failed = true;
                    continue;
                }

                if (
                    method
                        .GetAttributes()
                        .Any(a => a.AttributeClass?.Name is "BeforeHandlerAttribute" or "SkipBeforeHandlerAttribute")
                )
                {
                    parser.Report(
                        DiagnosticDescriptors.InvalidEndpoint,
                        method.Locations.FirstOrDefault(),
                        "configure minimal endpoint hooks through OnBeforeHandler/OnAfterHandler"
                    );
                    failed = true;
                    continue;
                }

                parser._endpointMethod = method;
                var invokerName = "__Endpoint_" + method.Name;
                var operationName = invokerName + "_Operation";
                var routes = new List<RouteInfo>
                {
                    new(0, "/" + string.Join("/", method.Parameters.Select(p => "{" + p.Name + "}")), false)
                };
                var invoker = parser.GenerateInvoker(
                    types.WebApiController,
                    method,
                    routes,
                    jsonContext,
                    invokerName,
                    operationName,
                    method.Locations.FirstOrDefault(),
                    null,
                    false,
                    false
                );

                if (invoker == null)
                {
                    failed = true;
                    continue;
                }

                var cache = parser.ReadOutputCache(owner, method);
                var idempotency = parser.ReadIdempotency(owner, method);
                parser._members
                    .Append("        /// <summary>Generated endpoint binding for ")
                    .Append(method.Name)
                    .Append(".</summary>\n")
                    .Append("        public static global::EmbedIO.Endpoints.EndpointHandler ")
                    .Append(Literals.Identifier(name))
                    .Append(" { get; } = new global::EmbedIO.Endpoints.EndpointHandler(")
                    .Append(invokerName)
                    .Append(", ")
                    .Append(operationName)
                    .Append(", ")
                    .Append(browser.Code ?? "null")
                    .Append(", ")
                    .Append(cache == null ? "null" : Literals.String(cache))
                    .Append(", ")
                    .Append(idempotency == null ? "null" : Literals.String(idempotency))
                    .Append(", ")
                    .Append(security.Name == null ? "null" : Literals.String(security.Name))
                    .Append(");\n\n")
                    .Append(invoker);
            }

            return new BindingModel(
                Literals.HintName(owner) + ".Endpoints.g.cs",
                failed ? null : parser.Wrap(),
                parser._diagnostics.ToImmutableArray(),
                parser._trimDiagnostics.ToImmutableArray()
            );
        }
    }
}
