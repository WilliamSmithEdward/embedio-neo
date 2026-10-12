using System;
#if NET10_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif

namespace EmbedIO.WebApi
{
    // Whether request-data attributes may convert values to a runtime Type, which looks up
    // type converters through TypeDescriptor. Only the reflection-based controller binder
    // uses those paths; generated bindings convert with converters chosen at build time.
    internal static class ReflectionBinding
    {
#if NET10_0_OR_GREATER
        internal const string Switch = "EmbedIO.WebApi.IsReflectionBindingSupported";

        // A feature switch, so trimming tools may substitute it. Unset, it is enabled.
        [FeatureSwitchDefinition(Switch)]
        [FeatureGuard(typeof(RequiresUnreferencedCodeAttribute))]
        [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
        internal static bool IsSupported { get; } = !AppContext.TryGetSwitch(Switch, out var enabled) || enabled;
#else
        internal static bool IsSupported => true;
#endif

        internal static NotSupportedException Disabled() => new(
            "Converting request data to a runtime type needs reflection, which is disabled in this application. "
            + "Register controllers through a generated binding (GeneratedControllerBindingAttribute) instead.");
    }
}
