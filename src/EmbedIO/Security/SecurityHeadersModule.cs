using System;
using System.Threading.Tasks;

namespace EmbedIO.Security
{
    /// <summary>Selects a response policy for following modules. Register before handlers and output caches.</summary>
    public sealed class SecurityHeadersModule : WebModuleBase
    {
        private readonly SecurityHeadersPolicy _policy;

        /// <summary>Creates scoped security-header middleware.</summary>
        /// <param name="baseRoute">The protected route scope.</param>
        /// <param name="policy">The immutable policy.</param>
        public SecurityHeadersModule(string baseRoute, SecurityHeadersPolicy policy) : base(baseRoute)
        {
            policy = Utilities.Validate.NotNull(nameof(policy), policy);
            _policy = policy;
        }

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <inheritdoc />
        protected override Task OnRequestAsync(IHttpContext context)
        {
            _policy.Apply(context);
            return Task.CompletedTask;
        }
    }
}
