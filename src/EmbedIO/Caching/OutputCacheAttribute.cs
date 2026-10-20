using System;

namespace EmbedIO.Caching
{
    /// <summary>Opts a controller action into a named public output-cache policy.</summary>
    /// <remarks>A method attribute overrides the class attribute. A null policy disables caching
    /// for that action. Authorization, browser protection and before-handler hooks always run.
    /// Streaming result types bypass caching. Configure named policies before registering controllers.</remarks>
    /// <param name="policy">The exact registered name, or null to disable caching for this action.</param>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public sealed class OutputCacheAttribute(string? policy) : Attribute
    {
        /// <summary>Gets the selected policy name.</summary>
        public string? Policy { get; } = policy;
    }
}
