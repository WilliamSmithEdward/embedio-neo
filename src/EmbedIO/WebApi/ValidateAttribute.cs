using System;

namespace EmbedIO.WebApi
{
    /// <summary>Enables bounded DataAnnotations validation of one bound action parameter and its public properties.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class ValidateAttribute : Attribute
    {
        /// <summary>Gets or sets the maximum visited value depth, including the root (default 16).</summary>
        public int MaxDepth { get; set; } = 16;

        /// <summary>Gets or sets the maximum visited values, including collection elements (default 1024).</summary>
        public int MaxNodes { get; set; } = 1024;

        /// <summary>Gets or sets the maximum returned errors (default 32).</summary>
        public int MaxErrors { get; set; } = 32;

        internal void CheckLimits()
        {
            if (MaxDepth < 1 || MaxDepth > 64 || MaxNodes < 1 || MaxNodes > 100000 || MaxErrors < 1 || MaxErrors > 1024)
                throw new InvalidOperationException("Validation limits must be positive: depth <= 64, nodes <= 100000, errors <= 1024.");
        }
    }
}
