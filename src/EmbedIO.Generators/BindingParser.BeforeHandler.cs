using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed partial class BindingParser
    {
        private (bool Valid, string? Code, bool Awaits) ReadBeforeHandler(
            INamedTypeSymbol controller,
            IMethodSymbol action
        )
        {
            var attributeType = _compilation.GetTypeByMetadataName("EmbedIO.WebApi.BeforeHandlerAttribute");
            var attribute = InheritedAttributes(action).FirstOrDefault(
                a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType)
            );

            for (var type = controller; type is not null && attribute is null; type = type.BaseType)
                attribute = type
                    .GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));

            if (attribute is null) return (true, null, false);

            var behavior = 0;

            foreach (var pair in attribute.NamedArguments)
                if (pair.Key == "Behavior" && pair.Value.Value is int value) behavior = value;

            var name = attribute.ConstructorArguments.Length == 1
                ? attribute.ConstructorArguments[0].Value as string
                : null;
            string? problem = null;
            IMethodSymbol? hook = null;

            if (behavior < 0 || behavior > 2) problem = "unknown behavior";

            else if (behavior == 2 && name != null) problem = "Skip cannot name a hook";

            else if (behavior != 2)
            {
                if (name is null || string.IsNullOrWhiteSpace(name)) problem = "a hook name is required";

                else
                {
                    var hooks = new List<IMethodSymbol>();

                    for (var type = controller; type is not null; type = type.BaseType)
                        foreach (var method in type
                            .GetMembers(name)
                            .OfType<IMethodSymbol>())
                            if (
                                method.MethodKind == MethodKind.Ordinary
                                && !method.IsStatic
                                && method.DeclaredAccessibility == Accessibility.Public
                                && !hooks.Any(derived => HidesBySignature(derived, method))
                            ) hooks.Add(method);

                    if (hooks.Count != 1) problem = "the hook must identify one public instance method";

                    else
                    {
                        hook = hooks[0];
                        var token = _compilation.GetTypeByMetadataName("System.Threading.CancellationToken");

                        if (
                            hook.IsGenericMethod
                            || hook.ReturnsByRef
                            || hook.ReturnsByRefReadonly
                            || (!hook.ReturnsVoid
                                && !SymbolEqualityComparer.Default.Equals(hook.ReturnType, _types.Task))
                            || (hook.ReturnsVoid
                                && (hook.IsAsync
                                    || hook
                                        .GetAttributes()
                                        .Any(
                                            a => a.AttributeClass?.ToDisplayString()
                                                == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                                        )))
                            || (hook.Parameters.Length != 0
                                && (hook.Parameters.Length != 1
                                    || hook.Parameters[0].RefKind != RefKind.None
                                    || !SymbolEqualityComparer.Default.Equals(hook.Parameters[0].Type, token)))
                        )
                            problem =
                                "the hook must return void or Task, not async void, and accept no arguments or one CancellationToken";

                        else if (!_compilation.IsSymbolAccessibleWithin(hook, _context))
                            problem = "the hook must be accessible from the generated binding";
                    }
                }
            }

            if (problem != null)
            {
                Report(
                    DiagnosticDescriptors.InvalidBeforeHandler,
                    attribute.ApplicationSyntaxReference
                        ?.GetSyntax()
                        .GetLocation()
                        ?? action.Locations.FirstOrDefault(),
                    action.ToDisplayString(),
                    problem
                );
                return (false, null, false);
            }

            var body = new StringBuilder(
                "                __context.CancellationToken.ThrowIfCancellationRequested();\n"
            );

            if (behavior == 0)
                body.Append(
                    "                __controller.PreProcessRequest();\n                __context.CancellationToken.ThrowIfCancellationRequested();\n"
                );

            if (hook != null)
            {
                var call = CallExpression(
                    controller,
                    hook,
                    hook.Parameters.Length == 0
                        ? []
                        : ["__context.CancellationToken"]
                );

                if (hook.ReturnsVoid)
                    body
                        .Append("                ")
                        .Append(call)
                        .Append(";\n");

                else
                    body
                        .Append("                await (")
                        .Append(call)
                        .Append(
                            " ?? throw new global::System.InvalidOperationException(\"The before-handler hook returned a null task.\")).ConfigureAwait(false);\n"
                        );

                body.Append("                __context.CancellationToken.ThrowIfCancellationRequested();\n");
            }

            return (true, body.ToString(), hook != null && !hook.ReturnsVoid);
        }
    }
}
