using System;

namespace EmbedIO.Authorization
{
    /// <summary>Requires authentication and optional roles or a policy before controller activation.</summary>
    /// <remarks>Attributes combine with AND. Comma-separated roles within one attribute combine with OR.</remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class AuthorizeAttribute : Attribute
    {
        /// <summary>Requires an authenticated user.</summary>
        public AuthorizeAttribute() { }
        /// <summary>Requires an authenticated user and the specified policy.</summary>
        /// <param name="policy">The registered policy name.</param>
        public AuthorizeAttribute(string policy) => Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        /// <summary>Gets or sets the optional policy name.</summary>
        public string? Policy { get; set; }
        /// <summary>Gets or sets comma-separated role names, of which at least one must match.</summary>
        public string? Roles { get; set; }
    }
}
