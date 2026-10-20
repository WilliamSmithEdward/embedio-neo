using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace EmbedIO.WebApi
{
    /// <summary>Cached validation metadata shared by generated and reflection controller bindings.</summary>
    /// <remarks>Creates a validation plan. Collection plans use an element resolver; object plans use members.</remarks>
    /// <param name="type">The declared value type.</param>
    /// <param name="rules">Type validation attributes.</param>
    /// <param name="members">Public property accessors.</param>
    /// <param name="element">Collection element plan, or null.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RequestValidationPlan(
        Type type,
        ValidationAttribute[] rules,
        RequestValidationMember[] members,
        Func<RequestValidationPlan>? element = null
        )
    {
        internal Type Type { get; } = type ?? throw new ArgumentNullException(nameof(type));
        internal ValidationAttribute[] Rules { get; } = (ValidationAttribute[])(rules ?? throw new ArgumentNullException(nameof(rules))).Clone();
        internal RequestValidationMember[] Members { get; } = (RequestValidationMember[])(members ?? throw new ArgumentNullException(nameof(members))).Clone();
        internal Func<RequestValidationPlan>? Element { get; } = element;
    }

    /// <summary>A property accessor and its validation rules, constructed once during binding.</summary>
    /// <remarks>Creates a property validation entry.</remarks>
    /// <param name="name">CLR property name, used in error paths.</param>
    /// <param name="jsonName">Explicit JSON name, or null to apply the configured naming policy.</param>
    /// <param name="get">Property accessor.</param>
    /// <param name="rules">Property validation attributes.</param>
    /// <param name="plan">Declared property type's plan.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RequestValidationMember(
        string name,
        string? jsonName,
        Func<object, object?> get,
        ValidationAttribute[] rules,
        Func<RequestValidationPlan> plan
        )
    {
        internal string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
        internal string? JsonName { get; } = jsonName;
        internal Func<object, object?> Get { get; } = get ?? throw new ArgumentNullException(nameof(get));
        internal ValidationAttribute[] Rules { get; } = (ValidationAttribute[])(rules ?? throw new ArgumentNullException(nameof(rules))).Clone();
        internal Func<RequestValidationPlan> Plan { get; } = plan ?? throw new ArgumentNullException(nameof(plan));
    }
}
