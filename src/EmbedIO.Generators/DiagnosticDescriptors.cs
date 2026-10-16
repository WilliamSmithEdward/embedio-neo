using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal static class DiagnosticDescriptors
    {
        private const string Category = "EmbedIO.WebApi";

        internal static readonly DiagnosticDescriptor ClassNotPartial = new(
            "EMBEDIO001",
            "Generated controller bindings need a partial, non-generic class",
            "'{0}' must be a partial, non-generic class, inside partial, non-generic containing types, to receive generated controller bindings",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor InvalidControllerType = new(
            "EMBEDIO002",
            "Unsupported controller type",
            "'{0}' cannot have a generated binding: {1}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor DuplicateBinding = new(
            "EMBEDIO003",
            "Controller binding name conflict",
            "The generated binding property '{0}' conflicts with another controller or member of '{1}'",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor InvalidJsonContext = new(
            "EMBEDIO004",
            "Invalid JSON serializer context",
            "'{0}' must be an accessible class derived from System.Text.Json.Serialization.JsonSerializerContext",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor NoRoutes = new(
            "EMBEDIO005",
            "Controller has no routes",
            "'{0}' has no public instance methods with route attributes",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor UnsupportedRouteAttribute = new(
            "EMBEDIO006",
            "Unsupported route attribute",
            "Route attribute '{0}' on '{1}' is not supported by generated bindings; use RouteAttribute or BaseRouteAttribute",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor NullRoute = new(
            "EMBEDIO007",
            "Route is null",
            "A route attribute on '{0}' has a null route",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor IgnoredMethod = new(
            "EMBEDIO008",
            "Route attribute on a method that is not registered",
            "'{0}' has a route attribute but is not a public, non-generic instance method, so it is not registered",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor UnsupportedParameter = new(
            "EMBEDIO009",
            "Unsupported controller method parameter",
            "Parameter '{0}' of '{1}' cannot be bound: {2}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor JsonDataNeedsContext = new(
            "EMBEDIO010",
            "JSON request data needs a JsonSerializerContext in trimmed applications",
            "Parameter '{0}' of '{1}' is deserialized with EmbedIO's default JSON options, which need reflection; set JsonSerializerContext on the GeneratedControllerBinding attribute for trimmed and Native AOT applications",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor UnsupportedReturnType = new(
            "EMBEDIO011",
            "Unsupported controller method return type",
            "'{0}' cannot be bound: {1}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor InvalidAuthorization = new(
            "EMBEDIO013",
            "Invalid authorization attribute",
            "Authorization on '{0}' is invalid: {1}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor SerializedAwaitable = new(
            "EMBEDIO012",
            "Awaitable result is serialized as an object",
            "'{0}' returns {1}, which is serialized as an object rather than awaited, as with reflection-based registration; return Task or Task<T> instead",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
    }
}
