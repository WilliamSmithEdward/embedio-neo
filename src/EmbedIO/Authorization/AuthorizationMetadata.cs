using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbedIO.Authorization
{
    /// <summary>One authorization attribute's immutable requirements.</summary>
    public sealed class AuthorizationRequirement
    {
        /// <summary>Creates a requirement. Authentication, policy and role constraints combine with AND.</summary>
        /// <param name="policy">An optional registered policy name.</param>
        /// <param name="roles">Allowed role names; an empty array imposes no role restriction.</param>
        public AuthorizationRequirement(string? policy, string[] roles)
        {
            if (policy != null && string.IsNullOrWhiteSpace(policy)) throw new ArgumentException("A policy name cannot be blank.", nameof(policy));
            if (roles == null) throw new ArgumentNullException(nameof(roles));
            if (roles.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Role names cannot be blank.", nameof(roles));
            Policy = policy;
            Roles = Array.AsReadOnly((string[])roles.Clone());
        }
        /// <summary>Gets the optional policy name.</summary>
        public string? Policy { get; }
        /// <summary>Gets roles, of which at least one must match when nonempty.</summary>
        public IReadOnlyList<string> Roles { get; }
    }
    /// <summary>Immutable authorization metadata for runtime handlers and generated OpenAPI descriptions.</summary>
    public sealed class AuthorizationMetadata
    {
        /// <summary>Creates metadata. All requirements must succeed unless anonymous access overrides them.</summary>
        /// <param name="allowAnonymous">Whether authorization attributes are overridden.</param>
        /// <param name="requirements">Controller and method requirements, including inheritance.</param>
        public AuthorizationMetadata(bool allowAnonymous, params AuthorizationRequirement[] requirements)
        {
            if (requirements == null) throw new ArgumentNullException(nameof(requirements));
            if (requirements.Any(item => item == null)) throw new ArgumentException("Requirements cannot contain null.", nameof(requirements));
            AllowAnonymous = allowAnonymous;
            Requirements = Array.AsReadOnly((AuthorizationRequirement[])requirements.Clone());
        }
        /// <summary>Gets whether anonymous access overrides requirements.</summary>
        public bool AllowAnonymous { get; }
        /// <summary>Gets requirements combined with AND.</summary>
        public IReadOnlyList<AuthorizationRequirement> Requirements { get; }
    }
}
