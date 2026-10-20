using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace EmbedIO.WebApi
{
    /// <summary>Cached validation metadata shared by generated and reflection controller bindings.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RequestValidationPlan
    {
        /// <summary>Creates a validation plan. Collection plans use an element resolver; object plans use members.</summary>
        /// <param name="type">The declared value type.</param>
        /// <param name="rules">Type validation attributes.</param>
        /// <param name="members">Public property accessors.</param>
        /// <param name="element">Collection element plan, or null.</param>
        public RequestValidationPlan(
            Type type,
            ValidationAttribute[] rules,
            RequestValidationMember[] members,
            Func<RequestValidationPlan>? element = null
        )
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Rules = (ValidationAttribute[])(rules ?? throw new ArgumentNullException(nameof(rules))).Clone();
            Members = (RequestValidationMember[])(members ?? throw new ArgumentNullException(nameof(members))).Clone();
            Element = element;
        }

        internal Type Type { get; }
        internal ValidationAttribute[] Rules { get; }
        internal RequestValidationMember[] Members { get; }
        internal Func<RequestValidationPlan>? Element { get; }
    }

    /// <summary>A property accessor and its validation rules, constructed once during binding.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RequestValidationMember
    {
        /// <summary>Creates a property validation entry.</summary>
        /// <param name="name">CLR property name, used in error paths.</param>
        /// <param name="jsonName">Explicit JSON name, or null to apply the configured naming policy.</param>
        /// <param name="get">Property accessor.</param>
        /// <param name="rules">Property validation attributes.</param>
        /// <param name="plan">Declared property type's plan.</param>
        public RequestValidationMember(
            string name,
            string? jsonName,
            Func<object, object?> get,
            ValidationAttribute[] rules,
            Func<RequestValidationPlan> plan
        )
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            JsonName = jsonName;
            Get = get ?? throw new ArgumentNullException(nameof(get));
            Rules = (ValidationAttribute[])(rules ?? throw new ArgumentNullException(nameof(rules))).Clone();
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        }

        internal string Name { get; }
        internal string? JsonName { get; }
        internal Func<object, object?> Get { get; }
        internal ValidationAttribute[] Rules { get; }
        internal Func<RequestValidationPlan> Plan { get; }
    }
}
