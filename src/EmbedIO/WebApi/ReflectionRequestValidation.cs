using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;

namespace EmbedIO.WebApi
{
    internal static class ReflectionRequestValidation
    {
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        internal static RequestValidationParameter? For(ParameterInfo parameter)
        {
            var options = parameter.GetCustomAttribute<ValidateAttribute>();

            if (options == null) return null;

            options.CheckLimits();
            var plans = new Dictionary<Type, RequestValidationPlan>();
            var pending = new Queue<Type>();
            var known = new HashSet<Type>();

            Func<RequestValidationPlan> Reference(Type type)
            {
                type = Nullable.GetUnderlyingType(type) ?? type;

                if (known.Add(type)) pending.Enqueue(type);

                if (known.Count > 128)
                    throw new InvalidOperationException(
                        "Validation model graphs may contain at most 128 declared types."
                    );

                return () => plans[type];
            }

            var root = Reference(parameter.ParameterType);

            while (pending.Count > 0)
            {
                var type = pending.Dequeue();
                var rules = Attributes(type);
                var members = new List<RequestValidationMember>();
                Func<RequestValidationPlan>? element = null;

                if (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                {
                    var sequence = type
                        .GetInterfaces()
                        .Concat([type])
                        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

                    if (
                        sequence == null
                        || type
                            .GetInterfaces()
                            .Concat([type])
                            .Any(
                                i => i.IsGenericType
                                    && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                                        || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                            )
                    )
                        throw new InvalidOperationException(
                            "Validation supports typed sequences, not dictionaries or untyped enumerables."
                        );

                    element = Reference(sequence.GetGenericArguments()[0]);
                }

                else if (!Scalar(type))
                {
                    if (type == typeof(object) || type.IsInterface)
                        throw new InvalidOperationException("Validation requires a concrete declared model type.");

                    foreach (var property in type
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .GroupBy(p => p.Name)
                        .Select(group => group.First()))
                    {
                        if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0) continue;

                        if (property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always)
                            continue;

                        var value = Expression.Parameter(typeof(object), "value");
                        var getter = Expression
                            .Lambda<Func<object, object?>>(
                                Expression.Convert(
                                    Expression.Property(
                                        Expression.Convert(
                                            value,
                                            (property.DeclaringType
                                                ?? throw new InvalidOperationException(
                                                    "The validated property has no declaring type."
                                                ))
                                        ),
                                        property
                                    ),
                                    typeof(object)
                                ),
                                value
                            )
                            .Compile();
                        members.Add(
                            new RequestValidationMember(
                                property.Name,
                                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name,
                                getter,
                                Attributes(property),
                                Reference(property.PropertyType)
                            )
                        );
                    }
                }

                plans[type] = new RequestValidationPlan(type, rules, [.. members], element);
            }

            return new RequestValidationParameter(
                root(),
                [.. parameter.GetCustomAttributes<ValidationAttribute>(true)],
                options
            );
        }

        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        private static ValidationAttribute[] Attributes(MemberInfo member) =>
            [.. Attribute
                .GetCustomAttributes(member, typeof(ValidationAttribute), true)
                .Cast<ValidationAttribute>()];

        private static bool Scalar(Type type) =>
            type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(TimeSpan)
                || type == typeof(Guid)
                || type == typeof(Uri)
                || type.FullName == "System.DateOnly"
                || type.FullName == "System.TimeOnly";

        public static T ValidateBound<T>(
            T value,
            string name,
            RequestValidationParameter validation,
            CancellationToken token
        ) => validation.Validate(value, name, token);
    }
}
