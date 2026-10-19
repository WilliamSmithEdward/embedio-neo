using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace EmbedIO.WebApi
{
    internal static class ReflectionRequestBinding
    {
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        internal static Expression? Bind(ParameterInfo parameter, Expression controller, Expression route)
        {
            var attributes = parameter
                .GetCustomAttributes<Attribute>(true)
                .ToArray();
            var selected = attributes
                .Where(IsBinding)
                .ToArray();

            if (selected.Length == 0) return null;

            if (selected.Length != 1 || attributes.Any(a => a
                    .GetType()
                    .GetInterfaces()
                    .Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IRequestDataAttribute<>) || i.GetGenericTypeDefinition() == typeof(IRequestDataAttribute<,>)))))
                throw new InvalidOperationException(
                    "A typed request binding cannot be combined with another binding attribute."
                );

            var attribute = selected[0];

            if (
                attribute is FormObjectAttribute
                    && parameter.Member is MethodInfo action
                    && action
                        .GetParameters()
                        .Any(p => p
                            .GetCustomAttributes<Attribute>(true)
                            .Any(a => a is FormFieldAttribute or FormDataAttribute or JsonDataAttribute or MultipartFormAttribute))
            )
                throw new InvalidOperationException(
                    "FormObject cannot share a body with a legacy form, JSON or multipart binding; use one form DTO."
                );

            var valueType = parameter.ParameterType;

            if (attribute is RouteFieldAttribute && valueType.IsArray)
                throw new InvalidOperationException("RouteField requires a scalar type.");

            var name = parameter.Name ?? throw new InvalidOperationException("A bound parameter needs a name.");

            if (attribute is QueryObjectAttribute || attribute is FormObjectAttribute)
            {
                if (!valueType.IsClass || valueType.IsAbstract || valueType.GetConstructor(Type.EmptyTypes) == null)
                    throw new InvalidOperationException(
                        "A request DTO needs a concrete class and public parameterless constructor."
                    );

                var plan = typeof(ReflectionRequestBinding).GetMethod(
                    nameof(ObjectPlan),
                    BindingFlags.Static | BindingFlags.NonPublic
                )
                    ?? throw new InvalidOperationException("Object binding factory missing.");
                object? built;

                try
                {
                    built = plan
                        .MakeGenericMethod(valueType)
                        .Invoke(null, [attribute]);
                }

                catch (TargetInvocationException error) when (error.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(error.InnerException)
                        .Throw();
                    throw;
                }

                var method = typeof(RequestBindingServices).GetMethod(
                    attribute is FormObjectAttribute
                        ? nameof(RequestBindingServices.FormAsync)
                        : nameof(RequestBindingServices.Query)
                )
                    ?? throw new InvalidOperationException("Request binding method missing.");
                var call = Expression.Call(
                    method.MakeGenericMethod(valueType),
                    controller,
                    Expression.Constant(built, typeof(RequestObjectPlan<>).MakeGenericType(valueType)),
                    Expression.Constant(name)
                );
                return attribute is FormObjectAttribute
                    ? Expression.Call(typeof(ReflectionRequestBinding), nameof(Await), [valueType], call)
                    : call;
            }

            var factory = typeof(ReflectionRequestBinding).GetMethod(
                nameof(FieldPlan),
                BindingFlags.Static | BindingFlags.NonPublic
            )
                ?? throw new InvalidOperationException("Field binding factory missing.");
            object? field;

            try
            {
                field = factory
                    .MakeGenericMethod(valueType)
                    .Invoke(null, [parameter, attribute]);
            }

            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(error.InnerException)
                    .Throw();
                throw;
            }

            return Expression.Call(
                Expression.Constant(field, typeof(RequestFieldPlan<>).MakeGenericType(valueType)),
                attribute is RouteFieldAttribute
                    ? nameof(RequestFieldPlan<>.BindRoute)
                    : nameof(RequestFieldPlan<>.Bind),
                null,
                attribute is RouteFieldAttribute ? route : controller,
                Expression.Constant(name)
            );
        }

        private static T Await<T>(System.Threading.Tasks.Task<T> value) =>
            value
                .GetAwaiter()
                .GetResult();

        private static bool IsBinding(Attribute attribute) =>
            attribute is QueryObjectAttribute
                or FormObjectAttribute
                or HeaderFieldAttribute
                or CookieFieldAttribute
                or RouteFieldAttribute;

        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        private static RequestObjectPlan<T> ObjectPlan<T>(Attribute attribute) where T : class
        {
            var valueType = typeof(T);
            var members = new List<RequestObjectMember<T>>();

            var properties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

            for (var current = valueType; current != null; current = current.BaseType)
                foreach (var memberProperty in current.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                ))
                {
                    if (memberProperty.SetMethod?.IsPublic != true) continue;

                    if (properties.TryGetValue(memberProperty.Name, out var derived))
                    {
                        if (derived.SetMethod?.GetBaseDefinition() != memberProperty.SetMethod.GetBaseDefinition())
                            throw new InvalidOperationException("Hidden writable request properties are ambiguous.");

                        continue;
                    }

                    properties.Add(memberProperty.Name, memberProperty);
                }

            foreach (var memberProperty in properties.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (memberProperty.SetMethod?.IsPublic != true) continue;

                if (
                    memberProperty.GetMethod?.IsPublic != true
                    || memberProperty
                        .GetCustomAttributesData()
                        .Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute")
                    || memberProperty.GetIndexParameters().Length != 0
                    || memberProperty.SetMethod.ReturnParameter
                        .GetRequiredCustomModifiers()
                        .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit")
                )
                    throw new InvalidOperationException(
                        "Request DTOs need non-indexed public readable/writable properties; init-only setters are unsupported."
                    );

                var factory = typeof(ReflectionRequestBinding).GetMethod(
                    nameof(Member),
                    BindingFlags.Static | BindingFlags.NonPublic
                )
                    ?? throw new InvalidOperationException("Member binding factory missing.");

                try
                {
                    members.Add(
                        (RequestObjectMember<T>)(factory
                            .MakeGenericMethod(valueType, memberProperty.PropertyType)
                            .Invoke(null, [memberProperty])
                            ?? throw new InvalidOperationException("Member binding factory returned null."))
                    );
                }

                catch (TargetInvocationException error) when (error.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(error.InnerException)
                        .Throw();
                    throw;
                }
            }

            var create = Expression
                .Lambda<Func<T>>(Expression.New(valueType))
                .Compile();
            return new RequestObjectPlan<T>(
                create,
                [.. members],
                attribute is QueryObjectAttribute query
                    ? query.RejectUnknownFields
                    : ((FormObjectAttribute)attribute).RejectUnknownFields
            );
        }

        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        private static RequestObjectMember<T> Member<T, TValue>(PropertyInfo memberProperty) where T : class
        {
            var options = memberProperty.GetCustomAttribute<RequestFieldAttribute>(true);
            var target = Expression.Parameter(typeof(T), "target");
            var value = Expression.Parameter(typeof(TValue), "value");
            var setter = Expression
                .Lambda<Action<T, TValue>>(
                    Expression.Assign(Expression.Property(target, memberProperty), value),
                    target,
                    value
                )
                .Compile();
            return RequestObjectMember<T>.Create(
                options?.Name ?? memberProperty.Name,
                memberProperty.Name,
                setter,
                Parser<TValue>(),
                options?.Required ?? false,
                typeof(TValue).IsArray,
                options?.DuplicateValues ?? RequestDuplicateValues.Reject
            );
        }

        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        private static RequestFieldPlan<T> FieldPlan<T>(ParameterInfo parameter, Attribute attribute)
        {
            var header = attribute as HeaderFieldAttribute;
            var cookie = attribute as CookieFieldAttribute;
            var route = attribute as RouteFieldAttribute;

            if (route == null)
                RequestBindingValues.CheckToken(
                    header?.Name ?? cookie?.Name
                        ?? parameter.Name
                        ?? throw new InvalidOperationException("Field name missing.")
                );

            var missing = parameter.HasDefaultValue
                ? Expression.Constant(parameter.DefaultValue, typeof(T))
                : (Expression)Expression.Default(typeof(T));
            return new RequestFieldPlan<T>(
                header?.Name ?? cookie?.Name ?? route?.Name
                    ?? parameter.Name
                    ?? throw new InvalidOperationException("Field name missing."),
                cookie != null,
                header?.Required ?? cookie?.Required ?? route?.Required ?? false,
                typeof(T).IsArray,
                header?.DuplicateValues ?? cookie?.DuplicateValues ?? RequestDuplicateValues.Reject,
                Parser<T>(),
                Expression
                    .Lambda<Func<T>>(missing)
                    .Compile()
            );
        }

        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        private static Func<string[], T> Parser<T>()
        {
            var raw = Expression.Parameter(typeof(string[]), "raw");
            Expression body;

            if (typeof(T).IsArray)
            {
                var element = typeof(T).GetElementType()
                    ?? throw new InvalidOperationException("Missing array element type.");

                if (typeof(T).GetArrayRank() != 1 || typeof(T) != element.MakeArrayType())
                    throw new InvalidOperationException("Only single-dimensional request arrays are supported.");

                var value = Expression.Parameter(typeof(string), "value");
                var parser = Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(typeof(string), element),
                    Scalar(element, value),
                    value
                );
                body = Expression.Call(
                    typeof(RequestBindingServices),
                    nameof(RequestBindingServices.Array),
                    [element],
                    raw,
                    parser
                );
            }

            else body = Scalar(typeof(T), Expression.ArrayIndex(raw, Expression.Constant(0)));

            return Expression
                .Lambda<Func<string[], T>>(body, raw)
                .Compile();
        }

        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0056:Use index operator",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index."
        )]
        private static Expression Scalar(Type valueType, Expression value)
        {
            var underlying = Nullable.GetUnderlyingType(valueType);

            if (underlying != null)
                return Expression.Condition(
                    Expression.Call(typeof(string), nameof(string.IsNullOrEmpty), null, value),
                    Expression.Default(valueType),
                    Expression.Convert(Scalar(underlying, value), valueType)
                );

            if (valueType == typeof(string)) return value;

            var converter = valueType.IsEnum ? null : TypeDescriptor.GetConverter(valueType);

            if (valueType.IsEnum || converter?.CanConvertFrom(typeof(string)) == true)
                return Expression.Call(
                    typeof(RequestBindingServices),
                    nameof(RequestBindingServices.Parse),
                    [valueType],
                    value,
                    Expression.Constant(converter, typeof(TypeConverter))
                );

            var methods = valueType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "TryParse" && !m.IsGenericMethod && m.ReturnType == typeof(bool));
            MethodInfo? parse = null;

            foreach (var candidate in methods.OrderByDescending(m => m.GetParameters().Length))
            {
                var args = candidate.GetParameters();

                if (
                    (args.Length == 2 || args.Length == 3 && args[1].ParameterType == typeof(IFormatProvider))
                    && args[0].ParameterType == typeof(string)
                    && args[args.Length - 1].IsOut
                    && args[args.Length - 1].ParameterType == valueType.MakeByRefType()
                )
                {
                    parse = candidate;
                    break;
                }
            }

            if (parse != null)
            {
                var parsed = Expression.Variable(valueType, "parsed");
                var args = parse.GetParameters().Length == 3
                    ? new[]
                    {
                        value,
                        Expression.Constant(CultureInfo.InvariantCulture, typeof(IFormatProvider)),
                        parsed
                    }
                    : [value, parsed];
                return Expression.Block(
                    [parsed],
                    Expression.Condition(
                        Expression.Call(parse, args),
                        valueType.IsValueType
                            ? parsed
                            : Expression.Coalesce(
                                parsed,
                                Expression.Throw(Expression.New(typeof(FormatException)), valueType)
                            ),
                        Expression.Throw(Expression.New(typeof(FormatException)), valueType)
                    )
                );
            }

            throw new InvalidOperationException(
                "Request fields need a string converter or public TryParse(string, [IFormatProvider,] out T)."
            );
        }
    }
}
