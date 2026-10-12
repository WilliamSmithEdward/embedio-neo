#if NETSTANDARD2_0
// The .NET Standard 2.0 reference assemblies lack the trimming annotations. These internal
// copies let shared source carry them; only the .NET 10 asset is analyzed for trimming.
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresUnreferencedCodeAttribute : Attribute
    {
        public RequiresUnreferencedCodeAttribute(string message) => Message = message;

        public string Message { get; }

        public string? Url { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresDynamicCodeAttribute : Attribute
    {
        public RequiresDynamicCodeAttribute(string message) => Message = message;

        public string Message { get; }

        public string? Url { get; set; }
    }
}
#endif
