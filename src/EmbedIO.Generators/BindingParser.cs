using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbedIO.Generators
{
    // Reads one class marked with GeneratedControllerBindingAttribute and produces its
    // generated source. Each rule mirrors WebApiModuleBase's reflection binder, so a
    // generated binding registers the same routes and binds parameters the same way.
    internal sealed class BindingParser
    {
        private const string Services = "global::EmbedIO.WebApi.ControllerBindingServices";
        private const string TaskType = "global::System.Threading.Tasks.Task";

        // TypeDescriptor's intrinsic converters for types without a TypeConverterAttribute.
        private static readonly (string Type, string Converter)[] IntrinsicConverters =
        {
            ("System.Boolean", "System.ComponentModel.BooleanConverter"),
            ("System.Byte", "System.ComponentModel.ByteConverter"),
            ("System.SByte", "System.ComponentModel.SByteConverter"),
            ("System.Char", "System.ComponentModel.CharConverter"),
            ("System.Int16", "System.ComponentModel.Int16Converter"),
            ("System.UInt16", "System.ComponentModel.UInt16Converter"),
            ("System.Int32", "System.ComponentModel.Int32Converter"),
            ("System.UInt32", "System.ComponentModel.UInt32Converter"),
            ("System.Int64", "System.ComponentModel.Int64Converter"),
            ("System.UInt64", "System.ComponentModel.UInt64Converter"),
            ("System.Single", "System.ComponentModel.SingleConverter"),
            ("System.Double", "System.ComponentModel.DoubleConverter"),
            ("System.Decimal", "System.ComponentModel.DecimalConverter"),
            ("System.DateTime", "System.ComponentModel.DateTimeConverter"),
            ("System.DateTimeOffset", "System.ComponentModel.DateTimeOffsetConverter"),
            ("System.TimeSpan", "System.ComponentModel.TimeSpanConverter"),
            ("System.Guid", "System.ComponentModel.GuidConverter"),
            ("System.Uri", "System.UriTypeConverter"),
            ("System.Version", "System.ComponentModel.VersionConverter"),
            ("System.DateOnly", "System.ComponentModel.DateOnlyConverter"),
            ("System.TimeOnly", "System.ComponentModel.TimeOnlyConverter"),
            ("System.Int128", "System.ComponentModel.Int128Converter"),
            ("System.UInt128", "System.ComponentModel.UInt128Converter"),
            ("System.Half", "System.ComponentModel.HalfConverter"),
            ("System.Globalization.CultureInfo", "System.ComponentModel.CultureInfoConverter"),
        };

        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol _context;
        private readonly WellKnownTypes _types;
        private readonly List<DiagnosticInfo> _diagnostics = new();
        private readonly List<DiagnosticInfo> _trimDiagnostics = new();
        private readonly Dictionary<string, string> _converterFields = new(StringComparer.Ordinal);
        private readonly StringBuilder _fields = new();
        private readonly StringBuilder _members = new();

        private BindingParser(Compilation compilation, INamedTypeSymbol context, WellKnownTypes types)
        {
            _compilation = compilation;
            _context = context;
            _types = types;
        }

        internal static BindingModel? Parse(GeneratorAttributeSyntaxContext attributeContext, CancellationToken cancellationToken)
        {
            if (attributeContext.TargetSymbol is not INamedTypeSymbol context)
                return null;

            var compilation = attributeContext.SemanticModel.Compilation;
            var types = WellKnownTypes.Create(compilation);
            if (types is null)
                return null;

            var attributes = context.GetAttributes()
                .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, types.GeneratedControllerBinding))
                .Select(a => (Attribute: a, Reference: a.ApplicationSyntaxReference))
                .Where(a => a.Reference is not null)
                .OrderBy(a => a.Reference?.SyntaxTree.FilePath, StringComparer.Ordinal)
                .ThenBy(a => a.Reference?.Span.Start)
                .Select(a => a.Attribute)
                .ToArray();

            // A partial class can carry the attribute on several declarations; the first one generates.
            if (attributes.Length == 0 || !IsDeclaredOn(attributes[0], attributeContext.TargetNode))
                return null;

            var parser = new BindingParser(compilation, context, types);
            var source = parser.Generate(attributes, cancellationToken);
            return new BindingModel(Literals.HintName(context), source, parser._diagnostics.ToImmutableArray(), parser._trimDiagnostics.ToImmutableArray());
        }

        private static bool IsDeclaredOn(AttributeData attribute, SyntaxNode declaration)
        {
            var reference = attribute.ApplicationSyntaxReference;
            return reference is not null
                && reference.SyntaxTree == declaration.SyntaxTree
                && declaration.Span.Contains(reference.Span);
        }

        private string? Generate(AttributeData[] attributes, CancellationToken cancellationToken)
        {
            var attributeLocation = Location(attributes[0]);
            if (!IsPartialAndNonGeneric(_context))
            {
                Report(DiagnosticDescriptors.ClassNotPartial, attributeLocation, _context.ToDisplayString());
                return null;
            }

            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var failed = false;
            foreach (var attribute in attributes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failed |= !GenerateController(attribute, usedNames, cancellationToken);
            }

            return failed ? null : Wrap();
        }

        private bool GenerateController(AttributeData attribute, HashSet<string> usedNames, CancellationToken cancellationToken)
        {
            var location = Location(attribute);
            var controller = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as INamedTypeSymbol : null;
            if (controller is null || controller.TypeKind == TypeKind.Error)
            {
                Report(DiagnosticDescriptors.InvalidControllerType, location, "(unknown)", "the controller type is missing or cannot be resolved");
                return false;
            }

            var problem = ControllerTypeProblem(controller);
            if (problem is not null)
            {
                Report(DiagnosticDescriptors.InvalidControllerType, location, controller.ToDisplayString(), problem);
                return false;
            }

            if (!TryGetJsonContext(attribute, location, out var jsonContext))
                return false;

            var propertyName = controller.Name;
            // A member cannot carry its enclosing type's name (CS0542), and GetMembers does not
            // report the type itself, so the context's own name is compared here as well.
            if (!usedNames.Add(propertyName) || propertyName == _context.Name || !_context.GetMembers(propertyName).IsEmpty)
            {
                Report(DiagnosticDescriptors.DuplicateBinding, location, propertyName, _context.ToDisplayString());
                return false;
            }

            var controllerName = Literals.TypeName(controller);
            var routes = new List<string>();
            var invokers = new StringBuilder();
            var invokerCount = 0;
            var failed = false;
            foreach (var (method, routeAttributes) in RoutedMethods(controller, location, ref failed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operationName = "__EmbedIO_" + propertyName + "_Operation" + Literals.Int(invokerCount);
                var invokerName = "__EmbedIO_" + propertyName + "_Invoker" + Literals.Int(invokerCount++);
                var invoker = GenerateInvoker(controller, method, routeAttributes, jsonContext, invokerName, operationName, location);
                if (invoker is null)
                {
                    failed = true;
                    continue;
                }

                invokers.Append(invoker);
                var handlerId = Literals.String(method.GetDocumentationCommentId() ?? method.ToDisplayString());
                foreach (var route in routeAttributes)
                {
                    routes.Add("new global::EmbedIO.WebApi.WebApiRouteBinding<" + controllerName + ">((global::EmbedIO.HttpVerbs)("
                        + Literals.Int(route.Verb) + "), " + Literals.String(route.Route) + ", " + (route.IsBaseRoute ? "true" : "false")
                        + ", " + handlerId + ", " + invokerName + (_types.WebApiOperation is null ? string.Empty : ", " + operationName) + ")");
                }
            }

            if (failed)
                return false;

            if (routes.Count == 0)
            {
                Report(DiagnosticDescriptors.NoRoutes, location, controller.ToDisplayString());
                return false;
            }

            var bindingType = "global::EmbedIO.WebApi.WebApiControllerBinding<" + controllerName + ">";
            var accessibility = IsEffectivelyPublic(controller) ? "public" : "internal";
            _members.Append("        /// <summary>The generated binding of <see cref=\"").Append(controllerName).Append("\"/>.</summary>\n")
                .Append("        ").Append(accessibility).Append(" static ").Append(bindingType).Append(' ').Append(Literals.Identifier(propertyName))
                .Append(" { get; } = new ").Append(bindingType).Append("(\n            ")
                .Append(string.Join(",\n            ", routes)).Append(");\n\n")
                .Append(invokers);
            return true;
        }

        private string? ControllerTypeProblem(INamedTypeSymbol controller)
        {
            if (controller.TypeKind != TypeKind.Class)
                return "it is not a class";

            for (var type = controller; type is not null; type = type.ContainingType)
            {
                if (type.IsGenericType)
                    return "generic controller types are not supported";
            }

            if (SymbolEqualityComparer.Default.Equals(controller, _types.WebApiController) || !DerivesFrom(controller, _types.WebApiController))
                return "it does not derive from EmbedIO.WebApi.WebApiController";

            return _compilation.IsSymbolAccessibleWithin(controller, _context)
                ? null
                : "it is not accessible from " + _context.ToDisplayString();
        }

        private bool TryGetJsonContext(AttributeData attribute, Location? location, out INamedTypeSymbol? jsonContext)
        {
            jsonContext = null;
            foreach (var argument in attribute.NamedArguments)
            {
                if (!string.Equals(argument.Key, "JsonSerializerContext", StringComparison.Ordinal) || argument.Value.Value is null)
                    continue;

                jsonContext = argument.Value.Value as INamedTypeSymbol;
                if (jsonContext is null
                    || _types.JsonSerializerContext is null
                    || jsonContext.IsGenericType
                    || !DerivesFrom(jsonContext, _types.JsonSerializerContext)
                    || !_compilation.IsSymbolAccessibleWithin(jsonContext, _context))
                {
                    Report(DiagnosticDescriptors.InvalidJsonContext, location, argument.Value.Value.ToString() ?? "(unknown)");
                    return false;
                }
            }

            return true;
        }

        // The methods Type.GetMethods(BindingFlags.Instance | BindingFlags.Public) returns, in
        // declaration order from the controller to its bases, with their route attributes,
        // including those inherited from overridden declarations.
        private List<(IMethodSymbol Method, List<RouteInfo> Routes)> RoutedMethods(INamedTypeSymbol controller, Location? location, ref bool failed)
        {
            var result = new List<(IMethodSymbol, List<RouteInfo>)>();
            var covered = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var declared = new List<IMethodSymbol>();
            for (var type = controller; type is not null; type = type.BaseType)
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.MethodKind != MethodKind.Ordinary || covered.Contains(method))
                        continue;

                    var eligible = !method.IsStatic && method.DeclaredAccessibility == Accessibility.Public;
                    if (eligible && declared.Any(d => HidesBySignature(d, method)))
                        continue;

                    var routeAttributes = InheritedAttributes(method).Where(a => DerivesFromOrEquals(a.AttributeClass, _types.RouteAttribute)).ToArray();
                    if (eligible)
                    {
                        declared.Add(method);
                        for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                            covered.Add(overridden);
                    }

                    if (routeAttributes.Length == 0)
                        continue;

                    if (!eligible || method.IsGenericMethod)
                    {
                        if (method.Locations.Any(l => l.IsInSource))
                            Report(DiagnosticDescriptors.IgnoredMethod, method.Locations.First(l => l.IsInSource), method.ToDisplayString());

                        continue;
                    }

                    var routes = new List<RouteInfo>();
                    foreach (var routeAttribute in routeAttributes)
                    {
                        var route = ReadRoute(routeAttribute, method, location);
                        if (route is null)
                            failed = true;
                        else
                            routes.Add(route);
                    }

                    result.Add((method, routes));
                }
            }

            return result;
        }

        private static bool HidesBySignature(IMethodSymbol derived, IMethodSymbol baseMethod)
            => string.Equals(derived.Name, baseMethod.Name, StringComparison.Ordinal)
                && derived.TypeParameters.Length == baseMethod.TypeParameters.Length
                && derived.Parameters.Length == baseMethod.Parameters.Length
                && derived.Parameters.Zip(baseMethod.Parameters, (d, b) => d.RefKind == b.RefKind && SymbolEqualityComparer.Default.Equals(d.Type, b.Type)).All(x => x);

        private RouteInfo? ReadRoute(AttributeData attribute, IMethodSymbol method, Location? fallback)
        {
            var location = Location(attribute) ?? fallback;
            var isBaseRouteAttribute = SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _types.BaseRouteAttribute);
            if (!isBaseRouteAttribute && !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _types.RouteAttribute))
            {
                Report(DiagnosticDescriptors.UnsupportedRouteAttribute, location, attribute.AttributeClass?.ToDisplayString() ?? "(unknown)", method.ToDisplayString());
                return null;
            }

            var arguments = attribute.ConstructorArguments;
            if (arguments.Length < 2 || arguments[0].Value is not int verb)
            {
                Report(DiagnosticDescriptors.UnsupportedRouteAttribute, location, attribute.AttributeClass?.ToDisplayString() ?? "(unknown)", method.ToDisplayString());
                return null;
            }

            if (arguments[1].Value is not string route)
            {
                Report(DiagnosticDescriptors.NullRoute, location, method.ToDisplayString());
                return null;
            }

            var isBaseRoute = isBaseRouteAttribute || (arguments.Length > 2 && arguments[2].Value is true);
            return new RouteInfo(verb, route, isBaseRoute);
        }

        private string? GenerateInvoker(INamedTypeSymbol controller, IMethodSymbol method, List<RouteInfo> routes, INamedTypeSymbol? jsonContext, string invokerName, string operationName, Location? fallback)
        {
            var methodLocation = method.Locations.FirstOrDefault(l => l.IsInSource) ?? fallback;
            var setup = new StringBuilder();
            var body = new StringBuilder();
            var arguments = new List<string>();
            var metadata = new List<string>();
            var awaits = false;
            var failed = false;
            var routeParameters = RouteParameterNames(routes);

            for (var i = 0; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];
                var argument = "__arg" + Literals.Int(i);
                var binding = BindParameter(controller, method, parameter, i, jsonContext, routeParameters, setup, argument, ref awaits, out var problem);
                if (binding is null)
                {
                    Report(DiagnosticDescriptors.UnsupportedParameter, parameter.Locations.FirstOrDefault(l => l.IsInSource) ?? methodLocation, parameter.Name, method.ToDisplayString(), problem ?? "unsupported");
                    failed = true;
                    continue;
                }

                body.Append("                ").Append(Literals.TypeName(parameter.Type)).Append(' ').Append(argument).Append(" = ").Append(binding).Append(";\n");
                arguments.Add(argument);
                if (_meta is not null)
                    metadata.Add(ParameterExpression(parameter, _meta));
            }

            var call = CallExpression(controller, method, arguments);
            var work = WorkExpression(method, call, methodLocation);
            if (failed || work is null)
                return null;

            if (_types.WebApiOperation is not null)
            {
                _fields.Append("        private static readonly global::EmbedIO.WebApi.WebApiOperation ").Append(operationName)
                    .Append(" = ").Append(OperationExpression(controller, method, metadata)).Append(";\n\n");
            }

            var invoker = new StringBuilder();
            var invokerType = "global::EmbedIO.WebApi.WebApiRouteInvoker<" + Literals.TypeName(controller) + ">";
            invoker.Append("        private static ").Append(invokerType).Append(' ').Append(invokerName).Append("()\n        {\n")
                .Append(setup)
                .Append("            return ").Append(awaits ? "async " : string.Empty).Append("(__controller, __context, __route, __serializer) =>\n            {\n")
                .Append(body);

            if (work.Value.Statement is not null)
                invoker.Append("                ").Append(work.Value.Statement).Append(";\n");

            invoker.Append("                return ")
                .Append(awaits ? work.Value.Task : "global::System.Threading.Tasks.Task.FromResult<" + TaskType + ">(" + work.Value.Task + ")")
                .Append(";\n            };\n        }\n\n");
            return invoker.ToString();
        }

        // Calls through the declaring type, so overload resolution cannot pick a derived overload.
        private static string CallExpression(INamedTypeSymbol controller, IMethodSymbol method, List<string> arguments)
        {
            var target = SymbolEqualityComparer.Default.Equals(method.ContainingType, controller)
                ? "__controller"
                : "((" + Literals.TypeName(method.ContainingType) + ")__controller)";
            return target + "." + Literals.Identifier(method.Name) + "(" + string.Join(", ", arguments) + ")";
        }

        // How the reflection binder turns the method's return value into the handler's task.
        private (string? Statement, string Task)? WorkExpression(IMethodSymbol method, string call, Location? location)
        {
            if (method.ReturnsByRef || method.ReturnsByRefReadonly)
            {
                Report(DiagnosticDescriptors.UnsupportedReturnType, location, method.ToDisplayString(), "methods returning by reference are not supported");
                return null;
            }

            var returnType = method.ReturnType;
            if (method.ReturnsVoid)
                return (call, TaskType + ".CompletedTask");

            if (SymbolEqualityComparer.Default.Equals(returnType, _types.Task))
                return (null, call);

            if (returnType is INamedTypeSymbol named && named.IsGenericType && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _types.TaskOfT))
                return (null, Services + ".SerializeResultAsync<" + Literals.TypeName(named.TypeArguments[0]) + ">(__serializer, __context, " + call + ")");

            if (returnType.IsRefLikeType || returnType.TypeKind == TypeKind.Pointer || returnType.TypeKind == TypeKind.FunctionPointer)
            {
                Report(DiagnosticDescriptors.UnsupportedReturnType, location, method.ToDisplayString(), "the return type cannot be passed to the response serializer");
                return null;
            }

            if (IsValueTask(returnType))
                Report(DiagnosticDescriptors.SerializedAwaitable, location, method.ToDisplayString(), returnType.ToDisplayString());

            return (null, "__serializer(__context, (object)" + call + ")");
        }

        private bool IsValueTask(ITypeSymbol type)
            => SymbolEqualityComparer.Default.Equals(type, _types.ValueTask)
                || (type is INamedTypeSymbol named && named.IsGenericType && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _types.ValueTaskOfT));

        // Returns the expression that binds one parameter, or null with a reason.
        private string? BindParameter(
            INamedTypeSymbol controller,
            IMethodSymbol method,
            IParameterSymbol parameter,
            int ordinal,
            INamedTypeSymbol? jsonContext,
            HashSet<string> routeParameters,
            StringBuilder setup,
            string argument,
            ref bool awaits,
            out string? problem)
        {
            problem = null;
            _meta = null;
            var type = parameter.Type;
            if (parameter.RefKind != RefKind.None)
            {
                problem = "ref, out and in parameters are not supported";
                return null;
            }

            if (type.IsRefLikeType || type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.FunctionPointer || type.TypeKind == TypeKind.Error)
            {
                problem = "its type is not supported";
                return null;
            }

            if (!_compilation.IsSymbolAccessibleWithin(type, _context))
            {
                problem = "its type is not accessible from " + _context.ToDisplayString();
                return null;
            }

            var typeName = Literals.TypeName(type);
            var name = Literals.String(parameter.Name);
            var attributes = InheritedParameterAttributes(method, ordinal);

            // 1. Request data attributes with an IRequestDataAttribute<TController, TData> interface.
            var genericCandidates = RequestDataInterfaces(attributes, _types.RequestDataOfControllerAndData);
            var failedRequestData = false;
            if (genericCandidates.Count > 0)
            {
                var matches = genericCandidates
                    .Where(c => IsAssignable(controller, c.Interface.TypeArguments[0]) && IsAssignable(c.Interface.TypeArguments[1], type))
                    .ToArray();
                if (matches.Length > 1)
                {
                    problem = "more than one request data attribute interface can provide its value";
                    return null;
                }

                if (matches.Length == 1)
                {
                    awaits |= !IsBuiltIn(matches[0].Attribute, _types.QueryField);
                    return GenericRequestData(matches[0].Attribute, matches[0].Interface, parameter, setup, argument, ref problem);
                }

                failedRequestData = true;
            }

            // 2. Request data attributes with an IRequestDataAttribute<TController> interface.
            var candidates = RequestDataInterfaces(attributes, _types.RequestDataOfController)
                .Where(c => IsAssignable(controller, c.Interface.TypeArguments[0]))
                .ToArray();
            if (candidates.Length > 1)
            {
                problem = "more than one request data attribute can provide its value";
                return null;
            }

            if (candidates.Length == 1)
            {
                var binding = RequestData(candidates[0].Attribute, candidates[0].Interface, parameter, jsonContext, method, setup, argument, ref awaits, out problem);
                return binding;
            }

            if (failedRequestData || RequestDataInterfaces(attributes, _types.RequestDataOfController).Count > 0)
            {
                problem = "no request data attribute on it can provide its type";
                return null;
            }

            // 3. A route parameter with the same name, or the default value.
            var defaultValue = parameter.HasExplicitDefaultValue
                ? Literals.Constant(parameter.ExplicitDefaultValue, type)
                : "default(" + typeName + ")";
            _meta = new ParameterMeta(ParameterSource.RouteOrDefault, name, false, null);
            if (!TryGetConverter(type, out var converter))
            {
                if (!routeParameters.Contains(parameter.Name))
                    return defaultValue;

                problem = "a route parameter cannot be converted to its type";
                return null;
            }

            var routeValue = "__routeValue" + argument.Substring("__arg".Length);
            return Services + ".TryGetRouteValue(__route, " + name + ", out var " + routeValue + ") ? "
                + Services + ".ConvertRouteValue<" + typeName + ">(" + routeValue + ", " + converter + ") : " + defaultValue;
        }

        private string? GenericRequestData(AttributeData attribute, INamedTypeSymbol requestInterface, IParameterSymbol parameter, StringBuilder setup, string argument, ref string? problem)
        {
            var dataType = requestInterface.TypeArguments[1];
            var isQuery = IsBuiltIn(attribute, _types.QueryField);
            if (isQuery || IsBuiltIn(attribute, _types.FormField))
            {
                if (!TryReadField(attribute, parameter, out var fieldName, out var badRequestIfMissing, ref problem))
                    return null;

                var many = dataType is IArrayTypeSymbol;
                var helper = isQuery
                    ? (many ? "QueryFieldStrings" : "QueryFieldString")
                    : (many ? "FormFieldStringsAsync" : "FormFieldStringAsync");
                var call = Services + "." + helper + "(__controller, " + fieldName + ", " + badRequestIfMissing + ")";
                _meta = new ParameterMeta(isQuery ? ParameterSource.Query : ParameterSource.Form, fieldName, badRequestIfMissing == "true", null);
                return isQuery ? call : "await " + call + ".ConfigureAwait(false)";
            }

            _meta = IsBuiltIn(attribute, _types.QueryData) ? new ParameterMeta(ParameterSource.QueryCollection, Literals.String(parameter.Name), false, null)
                : IsBuiltIn(attribute, _types.FormData) ? new ParameterMeta(ParameterSource.FormCollection, Literals.String(parameter.Name), false, null)
                : CustomMeta(attribute, parameter);
            var instance = CreateAttributeInstance(attribute, setup, argument, ref problem);
            return instance is null
                ? null
                : "await ((" + Literals.TypeName(requestInterface) + ")" + instance + ").GetRequestDataAsync(__controller, " + Literals.String(parameter.Name) + ").ConfigureAwait(false)";
        }

        private string? RequestData(
            AttributeData attribute,
            INamedTypeSymbol requestInterface,
            IParameterSymbol parameter,
            INamedTypeSymbol? jsonContext,
            IMethodSymbol method,
            StringBuilder setup,
            string argument,
            ref bool awaits,
            out string? problem)
        {
            problem = null;
            var type = parameter.Type;
            var typeName = Literals.TypeName(type);
            var name = Literals.String(parameter.Name);
            var isQuery = IsBuiltIn(attribute, _types.QueryField);
            if (isQuery || IsBuiltIn(attribute, _types.FormField))
            {
                if (!TryReadField(attribute, parameter, out var fieldName, out var badRequestIfMissing, ref problem))
                    return null;

                string call;
                if (type is IArrayTypeSymbol array)
                {
                    if (!array.IsSZArray || !TryGetConverter(array.ElementType, out var elementConverter))
                    {
                        problem = array.IsSZArray ? "field values cannot be converted to its element type" : "multi-dimensional arrays are not supported";
                        return null;
                    }

                    call = Services + "." + (isQuery ? "QueryFieldArray" : "FormFieldArrayAsync") + "<" + Literals.TypeName(array.ElementType) + ">(__controller, "
                        + fieldName + ", " + badRequestIfMissing + ", " + elementConverter + ")";
                }
                else
                {
                    if (!TryGetConverter(type, out var converter))
                    {
                        problem = "field values cannot be converted to its type";
                        return null;
                    }

                    call = Services + "." + (isQuery ? "QueryField" : "FormFieldAsync") + "<" + typeName + ">(__controller, "
                        + fieldName + ", " + badRequestIfMissing + ", " + name + ", " + converter + ")";
                }

                _meta = new ParameterMeta(isQuery ? ParameterSource.Query : ParameterSource.Form, fieldName, badRequestIfMissing == "true", null);
                if (isQuery)
                    return call;

                awaits = true;
                return "await " + call + ".ConfigureAwait(false)";
            }

            awaits = true;
            if (IsBuiltIn(attribute, _types.JsonData))
            {
                // With a context, an empty body fails deserialization; default options read it as the default value.
                _meta = new ParameterMeta(ParameterSource.JsonBody, name, jsonContext is not null, null);
                if (jsonContext is not null)
                    return "await " + Services + ".JsonDataAsync<" + typeName + ">(__controller, " + Literals.TypeName(jsonContext) + ".Default).ConfigureAwait(false)";

                _trimDiagnostics.Add(DiagnosticInfo.Create(DiagnosticDescriptors.JsonDataNeedsContext, parameter.Locations.FirstOrDefault(l => l.IsInSource) ?? method.Locations.FirstOrDefault(), parameter.Name, method.ToDisplayString()));
                return "await " + Services + ".JsonDataAsync<" + typeName + ">(__controller, " + name + ").ConfigureAwait(false)";
            }

            _meta = CustomMeta(attribute, parameter);
            var instance = CreateAttributeInstance(attribute, setup, argument, ref problem);
            return instance is null
                ? null
                : Services + ".CastRequestData<" + typeName + ">(" + name + ", await ((" + Literals.TypeName(requestInterface) + ")" + instance
                    + ").GetRequestDataAsync(__controller, typeof(" + typeName + "), " + name + ").ConfigureAwait(false))";
        }

        // QueryFieldAttribute and FormFieldAttribute constructors: (), (fieldName), (badRequestIfMissing), (fieldName, badRequestIfMissing).
        private static bool TryReadField(AttributeData attribute, IParameterSymbol parameter, out string fieldName, out string badRequestIfMissing, ref string? problem)
        {
            string? field = parameter.Name;
            var missingIsBadRequest = false;
            var constructorParameters = attribute.AttributeConstructor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty;
            for (var i = 0; i < attribute.ConstructorArguments.Length && i < constructorParameters.Length; i++)
            {
                var value = attribute.ConstructorArguments[i].Value;
                if (string.Equals(constructorParameters[i].Name, "fieldName", StringComparison.Ordinal))
                {
                    field = value as string;
                    if (string.IsNullOrEmpty(field))
                    {
                        problem = "the attribute's field name is null or empty";
                        fieldName = badRequestIfMissing = string.Empty;
                        return false;
                    }
                }
                else if (string.Equals(constructorParameters[i].Name, "badRequestIfMissing", StringComparison.Ordinal))
                {
                    missingIsBadRequest = value is true;
                }
            }

            fieldName = Literals.String(field ?? parameter.Name);
            badRequestIfMissing = missingIsBadRequest ? "true" : "false";
            return true;
        }

        // Declares the attribute instance once per registration, as the reflection binder captures one per handler.
        private string? CreateAttributeInstance(AttributeData attribute, StringBuilder setup, string argument, ref string? problem)
        {
            var attributeClass = attribute.AttributeClass;
            var constructor = attribute.AttributeConstructor;
            if (attributeClass is null || constructor is null || !_compilation.IsSymbolAccessibleWithin(constructor, _context))
            {
                problem = "its request data attribute cannot be constructed from generated code";
                return null;
            }

            var arguments = new List<string>();
            for (var i = 0; i < attribute.ConstructorArguments.Length; i++)
            {
                var value = Literals.TypedConstant(attribute.ConstructorArguments[i]);
                if (value is null)
                {
                    problem = "an argument of its request data attribute cannot be reproduced";
                    return null;
                }

                arguments.Add("(" + Literals.TypeName(constructor.Parameters[i].Type) + ")" + value);
            }

            var initializers = new List<string>();
            foreach (var named in attribute.NamedArguments)
            {
                var value = Literals.TypedConstant(named.Value);
                if (value is null)
                {
                    problem = "a property of its request data attribute cannot be reproduced";
                    return null;
                }

                initializers.Add(Literals.Identifier(named.Key) + " = " + value);
            }

            var variable = "__attribute" + argument.Substring("__arg".Length);
            setup.Append("            var ").Append(variable).Append(" = new ").Append(Literals.TypeName(attributeClass))
                .Append('(').Append(string.Join(", ", arguments)).Append(')');
            if (initializers.Count > 0)
                setup.Append(" { ").Append(string.Join(", ", initializers)).Append(" }");

            setup.Append(";\n");
            return variable;
        }

        // The converter TypeDescriptor returns for the type (or a nullable type's underlying type):
        // null for strings and enumerations, which FromString converts without one.
        private bool TryGetConverter(ITypeSymbol type, out string converter)
        {
            converter = "null";
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                type = nullable.TypeArguments[0];

            if (type.SpecialType == SpecialType.System_String || type.TypeKind == TypeKind.Enum)
                return true;

            var metadataName = type.ContainingNamespace is null ? null : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;
            foreach (var (typeName, converterName) in IntrinsicConverters)
            {
                if (string.Equals(metadataName, typeName, StringComparison.Ordinal) && type.ContainingType is null)
                {
                    var converterType = _compilation.GetTypeByMetadataName(converterName);
                    if (converterType is null)
                        return false;

                    converter = ConverterField(converterType, "new " + Literals.TypeName(converterType) + "()");
                    return true;
                }
            }

            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                foreach (var attribute in current.GetAttributes())
                {
                    if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _types.TypeConverterAttribute))
                        continue;

                    if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol converterType
                        || converterType.IsAbstract || converterType.IsGenericType || !_compilation.IsSymbolAccessibleWithin(converterType, _context))
                        return false;

                    // TypeDescriptor prefers a constructor taking the target type.
                    var constructors = converterType.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToArray();
                    if (constructors.Any(c => c.Parameters.Length == 1 && c.Parameters[0].Type.ToDisplayString() == "System.Type"))
                    {
                        converter = ConverterField(converterType, "new " + Literals.TypeName(converterType) + "(typeof(" + Literals.TypeName(type) + "))");
                        return true;
                    }

                    if (constructors.Any(c => c.Parameters.Length == 0))
                    {
                        converter = ConverterField(converterType, "new " + Literals.TypeName(converterType) + "()");
                        return true;
                    }

                    return false;
                }
            }

            return false;
        }

        private string ConverterField(INamedTypeSymbol converterType, string creation)
        {
            if (!_converterFields.TryGetValue(creation, out var field))
            {
                field = "__EmbedIO_Converter" + Literals.Int(_converterFields.Count);
                _converterFields.Add(creation, field);
                _fields.Append("        private static readonly ").Append(Literals.TypeName(converterType)).Append(' ').Append(field)
                    .Append(" = ").Append(creation).Append(";\n\n");
            }

            return field;
        }

        private static List<(AttributeData Attribute, INamedTypeSymbol Interface)> RequestDataInterfaces(List<AttributeData> attributes, INamedTypeSymbol requestDataInterface)
        {
            var result = new List<(AttributeData, INamedTypeSymbol)>();
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeClass is null)
                    continue;

                foreach (var implemented in attribute.AttributeClass.AllInterfaces)
                {
                    if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, requestDataInterface))
                        result.Add((attribute, implemented));
                }
            }

            return result;
        }

        // Parameter attributes, including inheritable ones declared on overridden methods,
        // as ParameterInfo.GetCustomAttributes(inherit: true) returns them.
        private List<AttributeData> InheritedParameterAttributes(IMethodSymbol method, int ordinal)
        {
            var result = method.Parameters[ordinal].GetAttributes().ToList();
            for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                AddInherited(result, overridden.Parameters[ordinal].GetAttributes());

            return result;
        }

        private List<AttributeData> InheritedAttributes(IMethodSymbol method)
        {
            var result = method.GetAttributes().ToList();
            for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                AddInherited(result, overridden.GetAttributes());

            return result;
        }

        private void AddInherited(List<AttributeData> result, ImmutableArray<AttributeData> inherited)
        {
            foreach (var attribute in inherited)
            {
                var (allowMultiple, isInherited) = Usage(attribute.AttributeClass);
                if (!isInherited)
                    continue;

                if (!allowMultiple && result.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute.AttributeClass)))
                    continue;

                result.Add(attribute);
            }
        }

        private (bool AllowMultiple, bool Inherited) Usage(INamedTypeSymbol? attributeClass)
        {
            for (var type = attributeClass; type is not null; type = type.BaseType)
            {
                foreach (var attribute in type.GetAttributes())
                {
                    if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _types.AttributeUsageAttribute))
                        continue;

                    var allowMultiple = false;
                    var inherited = true;
                    foreach (var named in attribute.NamedArguments)
                    {
                        if (string.Equals(named.Key, "AllowMultiple", StringComparison.Ordinal))
                            allowMultiple = named.Value.Value is true;
                        else if (string.Equals(named.Key, "Inherited", StringComparison.Ordinal))
                            inherited = named.Value.Value is not false;
                    }

                    return (allowMultiple, inherited);
                }
            }

            return (false, true);
        }

        // Parameter names in route templates, used only to tell whether an unconvertible
        // parameter would ever receive a route value.
        private static HashSet<string> RouteParameterNames(List<RouteInfo> routes)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in routes)
            {
                var position = 0;
                while ((position = route.Route.IndexOf('{', position)) >= 0)
                {
                    var close = route.Route.IndexOf('}', position);
                    if (close < 0)
                        break;

                    names.Add(route.Route.Substring(position + 1, close - position - 1).TrimEnd('?'));
                    position = close;
                }
            }

            return names;
        }

        private static bool IsBuiltIn(AttributeData attribute, INamedTypeSymbol? builtIn)
            => builtIn is not null && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, builtIn);

        // Type.IsAssignableFrom for the reference types request data interfaces use.
        private bool IsAssignable(ITypeSymbol source, ITypeSymbol destination)
        {
            var conversion = _compilation.ClassifyConversion(source, destination);
            return conversion.IsIdentity || (conversion.IsImplicit && conversion.IsReference);
        }

        private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                    return true;
            }

            return false;
        }

        private static bool DerivesFromOrEquals(INamedTypeSymbol? type, INamedTypeSymbol baseType)
            => type is not null && (SymbolEqualityComparer.Default.Equals(type, baseType) || DerivesFrom(type, baseType));

        private static bool IsEffectivelyPublic(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility != Accessibility.Public)
                    return false;
            }

            return true;
        }

        private static bool IsPartialAndNonGeneric(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.ContainingType)
            {
                if (current.IsGenericType || current.DeclaringSyntaxReferences.IsEmpty)
                    return false;

                foreach (var reference in current.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax() is not TypeDeclarationSyntax declaration
                        || declaration is InterfaceDeclarationSyntax
                        || !declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                        return false;
                }
            }

            return type.TypeKind == TypeKind.Class;
        }

        private string Wrap()
        {
            var text = new StringBuilder();
            var containing = new List<INamedTypeSymbol>();
            for (var current = _context; current is not null; current = current.ContainingType)
                containing.Insert(0, current);

            var indent = string.Empty;
            if (!_context.ContainingNamespace.IsGlobalNamespace)
            {
                text.Append("namespace ").Append(_context.ContainingNamespace.ToDisplayString()).Append("\n{\n");
                indent = "    ";
            }

            foreach (var type in containing)
            {
                text.Append(indent).Append("partial ").Append(Keyword(type)).Append(' ').Append(Literals.Identifier(type.Name)).Append('\n')
                    .Append(indent).Append("{\n");
                indent += "    ";
            }

            // Members are written for a class inside a namespace (8 spaces); re-indent for the actual depth.
            foreach (var line in (_fields.ToString() + _members).TrimEnd('\n').Split('\n'))
            {
                if (line.Length == 0)
                    text.Append('\n');
                else if (indent.Length >= 8)
                    text.Append(new string(' ', indent.Length - 8)).Append(line).Append('\n');
                else
                    text.Append(line.Substring(8 - indent.Length)).Append('\n');
            }

            for (var i = containing.Count; i > 0; i--)
            {
                indent = indent.Substring(4);
                text.Append(indent).Append("}\n");
            }

            if (!_context.ContainingNamespace.IsGlobalNamespace)
                text.Append("}\n");

            return text.ToString();
        }

        private static string Keyword(INamedTypeSymbol type)
            => type.IsRecord
                ? (type.TypeKind == TypeKind.Struct ? "record struct" : "record")
                : (type.TypeKind == TypeKind.Struct ? "struct" : "class");

        private void Report(DiagnosticDescriptor descriptor, Location? location, params string[] arguments)
            => _diagnostics.Add(DiagnosticInfo.Create(descriptor, location, arguments));

        private static Location? Location(AttributeData attribute)
            => attribute.ApplicationSyntaxReference is { } reference
                ? reference.SyntaxTree.GetLocation(reference.Span)
                : null;

        // How the parameter that BindParameter last bound reaches the method, for operation metadata.
        private ParameterMeta? _meta;

        private static ParameterMeta CustomMeta(AttributeData attribute, IParameterSymbol parameter)
            => new(ParameterSource.Custom, Literals.String(parameter.Name), false, Literals.String(attribute.AttributeClass?.ToDisplayString() ?? "(unknown)"));

        private static string ParameterExpression(IParameterSymbol parameter, ParameterMeta meta)
            => "new global::EmbedIO.WebApi.WebApiOperationParameter(" + Literals.String(parameter.Name) + ", " + meta.FieldName
                + ", (global::EmbedIO.WebApi.WebApiParameterSource)(" + Literals.Int((int)meta.Source) + "), " + TypeOf(parameter.Type)
                + ", " + (meta.Required ? "true" : "false") + ", " + EnumNames(parameter.Type) + ", " + (meta.CustomAttribute ?? "null") + ")";

        private string OperationExpression(INamedTypeSymbol controller, IMethodSymbol method, List<string> parameters)
        {
            var attributes = InheritedAttributes(method);
            var operation = attributes.FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _types.OpenApiOperation));
            var responses = attributes.Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _types.OpenApiResponse))
                .Select(AttributeExpression).Where(e => e is not null).ToArray();
            var ignored = attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _types.OpenApiIgnore));
            for (var type = controller; type is not null && !ignored; type = type.BaseType)
                ignored = type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _types.OpenApiIgnore));

            return "new global::EmbedIO.WebApi.WebApiOperation(" + Literals.String(controller.Name) + ", " + Literals.String(method.Name) + ", "
                + ResultTypeOf(method) + ", new global::EmbedIO.WebApi.WebApiOperationParameter[] { " + string.Join(", ", parameters) + " }, "
                + (operation is null ? "null" : AttributeExpression(operation) ?? "null")
                + ", new global::EmbedIO.OpenApi.OpenApiResponseAttribute[] { " + string.Join(", ", responses) + " }, "
                + (ignored ? "true" : "false") + ")";
        }

        // The type the module's serializer receives, or null when the method returns no value.
        private string ResultTypeOf(IMethodSymbol method)
        {
            if (method.ReturnsVoid || SymbolEqualityComparer.Default.Equals(method.ReturnType, _types.Task))
                return "null";

            return method.ReturnType is INamedTypeSymbol named && named.IsGenericType && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, _types.TaskOfT)
                ? TypeOf(named.TypeArguments[0])
                : TypeOf(method.ReturnType);
        }

        private static string TypeOf(ITypeSymbol type)
            => type.TypeKind == TypeKind.Dynamic ? "typeof(object)" : "typeof(" + Literals.TypeName(type) + ")";

        // Enumeration member names for a value converted from a string, captured so documents need no reflection.
        private static string EnumNames(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
                type = array.ElementType;
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                type = nullable.TypeArguments[0];
            if (type.TypeKind != TypeKind.Enum)
                return "null";

            var names = type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => Literals.String(f.Name));
            return "new string[] { " + string.Join(", ", names) + " }";
        }

        // The attribute recreated from its constant arguments, or null if one cannot be expressed.
        private static string? AttributeExpression(AttributeData attribute)
        {
            if (attribute.AttributeClass is null || attribute.AttributeConstructor is null)
                return null;

            var arguments = new List<string>();
            for (var i = 0; i < attribute.ConstructorArguments.Length; i++)
            {
                var value = Literals.TypedConstant(attribute.ConstructorArguments[i]);
                if (value is null)
                    return null;
                arguments.Add("(" + Literals.TypeName(attribute.AttributeConstructor.Parameters[i].Type) + ")" + value);
            }

            var initializers = new List<string>();
            foreach (var named in attribute.NamedArguments)
            {
                var value = Literals.TypedConstant(named.Value);
                if (value is null)
                    return null;
                initializers.Add(Literals.Identifier(named.Key) + " = " + value);
            }

            return "new " + Literals.TypeName(attribute.AttributeClass) + "(" + string.Join(", ", arguments) + ")"
                + (initializers.Count > 0 ? " { " + string.Join(", ", initializers) + " }" : string.Empty);
        }

        // Matches EmbedIO.WebApi.WebApiParameterSource.
        private enum ParameterSource
        {
            RouteOrDefault,
            Query,
            QueryCollection,
            Form,
            FormCollection,
            JsonBody,
            Custom,
        }

        private sealed class ParameterMeta
        {
            internal ParameterMeta(ParameterSource source, string fieldName, bool required, string? customAttribute)
            {
                Source = source;
                FieldName = fieldName;
                Required = required;
                CustomAttribute = customAttribute;
            }

            internal ParameterSource Source { get; }

            // A C# string literal.
            internal string FieldName { get; }

            internal bool Required { get; }

            // A C# string literal, for custom request-data attributes.
            internal string? CustomAttribute { get; }
        }

        private sealed class RouteInfo
        {
            internal RouteInfo(int verb, string route, bool isBaseRoute)
            {
                Verb = verb;
                Route = route;
                IsBaseRoute = isBaseRoute;
            }

            internal int Verb { get; }

            internal string Route { get; }

            internal bool IsBaseRoute { get; }
        }
    }
}
