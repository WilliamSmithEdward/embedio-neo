using System;
using System.Threading.Tasks;

namespace EmbedIO.Security
{
    /// <summary>Opt-in browser protection before subsequent modules, including WebSocket upgrades.</summary>
    /// <remarks>Register after authentication and before handlers/CORS short-circuiting. The application owns
    /// the policy lifetime. Controller exemptions cannot undo an earlier module's token requirement.</remarks>
    public sealed class BrowserRequestProtectionModule : WebModuleBase
    {
        /// <summary>Creates middleware using an existing application-owned policy.</summary>
        /// <param name="baseRoute">Protected module scope.</param>
        /// <param name="protection">Policy shared with token issuance.</param>
        /// <param name="requireToken">Whether unsafe methods require tokens; defaults to true.</param>
        public BrowserRequestProtectionModule(
            string baseRoute,
            BrowserRequestProtection protection,
            bool requireToken = true
        ) : base(baseRoute)
        {
            _protection = protection ?? throw new ArgumentNullException(nameof(protection));
            _requireToken = requireToken;
        }

        private readonly BrowserRequestProtection _protection;
        private readonly bool _requireToken;

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <inheritdoc />
        protected override Task OnRequestAsync(IHttpContext context)
        {
            _protection.ValidateRequest(context, _requireToken);
            return Task.CompletedTask;
        }
    }
}
