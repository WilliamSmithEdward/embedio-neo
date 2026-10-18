using System;

namespace EmbedIO.Security
{
    /// <summary>Selects a named security-header policy. An action declaration overrides its inherited controller declaration.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public sealed class SecurityHeadersAttribute : Attribute
    {
        /// <summary>Creates a named policy selection. Configure the policy before controller registration.</summary>
        /// <param name="policy">A nonempty policy name of at most 128 visible ASCII characters.</param>
        public SecurityHeadersAttribute(string policy)
        {
            if (string.IsNullOrWhiteSpace(policy) || policy.Length > 128)
                throw new ArgumentException("A bounded policy name is required.", nameof(policy));

            foreach (var c in policy)
                if (c < 33 || c > 126)
                    throw new ArgumentException("Policy names require visible ASCII.", nameof(policy));

            Policy = policy;
        }

        /// <summary>Gets the named policy.</summary>
        public string Policy { get; }
    }
}
