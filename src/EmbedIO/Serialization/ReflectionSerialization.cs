using System;
#if NET10_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
#endif

namespace EmbedIO.Serialization
{
    // Whether reflection-based JSON (EmbedIO's default options) can run. Trimmed and Native
    // AOT applications disable it through System.Text.Json's feature switch; they pass
    // JsonTypeInfo or source-generated options instead.
    internal static class ReflectionSerialization
    {
        internal const string Message = "EmbedIO's default JSON options resolve type metadata by reflection. "
            + "Trimmed and Native AOT applications should use the overloads that take a JsonTypeInfo, or options with a JsonSerializerContext.";

#if NET10_0_OR_GREATER
        internal const string Switch = "EmbedIO.Serialization.IsReflectionEnabled";

        // A feature switch, so trimming tools may substitute it; unset, it follows
        // System.Text.Json's own switch, which trimmed and AOT applications turn off.
        [FeatureSwitchDefinition(Switch)]
        [FeatureGuard(typeof(RequiresUnreferencedCodeAttribute))]
        [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
        internal static bool IsSupported { get; } = AppContext.TryGetSwitch(Switch, out var enabled) ? enabled : JsonSerializer.IsReflectionEnabledByDefault;
#else
        internal static bool IsSupported => true;
#endif

        internal static NotSupportedException Disabled() => new(
            "Reflection-based JSON serialization is disabled in this application, for example by trimming or Native AOT. "
            + "Use the overloads that take a JsonTypeInfo, or JsonSerializerOptions whose TypeInfoResolver is a JsonSerializerContext.");
    }
}
