using System;

namespace EmbedIO.Caching
{
    /// <summary>Opts a controller action into a named public output-cache policy.</summary>
    /// <remarks>A method attribute overrides the class attribute. A null policy disables caching
    /// for that action. Authorization, browser protection and before-handler hooks always run.
    /// Streaming result types bypass caching. Configure named policies before registering controllers.</remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public sealed class OutputCacheAttribute : Attribute
    {
        /// <summary>Selects a named policy, or null to disable a class policy for this action.</summary>
        /// <param name="policy">The exact registered name, or null.</param>
        public OutputCacheAttribute(string? policy) => Policy = policy;

        /// <summary>Gets the selected policy name.</summary>
        public string? Policy { get; }
    }
}
