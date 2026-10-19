using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbedIO.WebApi
{
    /// <summary>Validation metadata for an explicitly opted-in controller parameter.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RequestValidationParameter
    {
        /// <summary>Creates cached parameter validation metadata.</summary>
        /// <param name="plan">Declared model plan.</param>
        /// <param name="rules">Parameter rules.</param>
        /// <param name="options">Traversal limits.</param>
        public RequestValidationParameter(
            RequestValidationPlan plan,
            ValidationAttribute[] rules,
            ValidateAttribute options
        )
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            Rules = (ValidationAttribute[])(rules ?? throw new ArgumentNullException(nameof(rules))).Clone();

            options = Utilities.Validate.NotNull(nameof(options), options);

            options.CheckLimits();
            Options = new ValidateAttribute
            {
                MaxDepth = options.MaxDepth,
                MaxNodes = options.MaxNodes,
                MaxErrors = options.MaxErrors
            };
        }

        internal RequestValidationPlan Plan { get; }
        internal ValidationAttribute[] Rules { get; }
        internal ValidateAttribute Options { get; }

        /// <summary>Validates a bound parameter and returns its original value.</summary>
        /// <typeparam name="T">Bound parameter type.</typeparam>
        /// <param name="value">Bound value.</param>
        /// <param name="name">Parameter name.</param>
        /// <param name="token">Request cancellation.</param>
        /// <returns>The bound value.</returns>
        public T Validate<T>(T value, string name, System.Threading.CancellationToken token = default) =>
            RequestValidation.Validate(value, name, Plan, Rules, Options, token);

        /// <summary>Gets validation metadata for a flattened request-object property.</summary>
        /// <param name="name">CLR property name.</param>
        /// <returns>Property validation metadata, or null for a missing member.</returns>
        public RequestValidationParameter? ForMember(string name)
        {
            var member = Plan.Members.FirstOrDefault(m => m.Name == name);
            return member == null ? null : new RequestValidationParameter(member.Plan(), member.Rules, Options);
        }

        internal JsonObject Schema(JsonSerializerOptions options) => Shape(Plan, Rules, options, [], 0);

        private static JsonObject Shape(
            RequestValidationPlan plan,
            ValidationAttribute[] rules,
            JsonSerializerOptions options,
            System.Collections.Generic.HashSet<Type> ancestors,
            int depth
        )
        {
            var schema = new JsonObject();

            foreach (var rule in plan.Rules.Concat(rules))
            {
                switch (rule)
                {
                    case StringLengthAttribute length when rule.GetType() == typeof(StringLengthAttribute):
                        schema["minLength"] = (length.MinimumLength + 1L) / 2;
                        schema["x-embedio-minLengthUtf16"] = length.MinimumLength;
                        schema["x-embedio-maxLengthUtf16"] = length.MaximumLength;
                        schema["maxLength"] = length.MaximumLength;
                        break;

                    case MinLengthAttribute min when rule.GetType() == typeof(MinLengthAttribute):
                        schema[plan.Type == typeof(string) ? "minLength" : "minItems"] = plan.Type == typeof(string)
                            ? (min.Length + 1L) / 2
                            : min.Length;

                        if (plan.Type == typeof(string)) schema["x-embedio-minLengthUtf16"] = min.Length;

                        break;

                    case MaxLengthAttribute max when rule.GetType() == typeof(MaxLengthAttribute) && max.Length >= 0:
                        schema[plan.Type == typeof(string) ? "maxLength" : "maxItems"] = max.Length;
                        break;

                    case RequiredAttribute when rule.GetType() == typeof(RequiredAttribute):
                        schema["not"] = new JsonObject { ["type"] = "null" };
                        break;

                    case RangeAttribute range when rule.GetType() == typeof(RangeAttribute)
                        && (range.Minimum is int || range.Minimum is double):
#if NET10_0_OR_GREATER
                        var minimumKey = range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum";
                        var maximumKey = range.MaximumIsExclusive ? "exclusiveMaximum" : "maximum";
#else
                        const string minimumKey = "minimum";
                        const string maximumKey = "maximum";
#endif
                        schema[minimumKey] = JsonValue.Create(
                            Convert.ToDouble(range.Minimum, System.Globalization.CultureInfo.InvariantCulture)
                        );
                        schema[maximumKey] = JsonValue.Create(
                            Convert.ToDouble(range.Maximum, System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                }
            }

            // Runtime-only rules are not translated into misleading schema constraints.
            if (depth >= 16 || !ancestors.Add(plan.Type))
            {
                schema["x-embedio-validation-runtime-only"] = true;
                return schema;
            }

            try
            {
                if (plan.Element != null)
                    schema["items"] = Shape(plan.Element(), [], options, ancestors, depth + 1);

                else if (plan.Members.Length > 0)
                {
                    var properties = new JsonObject();
                    var requiredProperties = new JsonArray();

                    foreach (var member in plan.Members)
                    {
                        var name = member.JsonName ?? options.PropertyNamingPolicy?.ConvertName(member.Name)
                            ?? member.Name;
                        properties[name] = Shape(member.Plan(), member.Rules, options, ancestors, depth + 1);

                        if (member.Rules.Any(rule => rule.GetType() == typeof(RequiredAttribute)))
                            requiredProperties.Add((JsonNode?)JsonValue.Create(name));
                    }

                    schema["properties"] = properties;

                    if (requiredProperties.Count > 0) schema["required"] = requiredProperties;
                }

                return schema;
            }

            finally
            {
                ancestors.Remove(plan.Type);
            }
        }
    }
}
