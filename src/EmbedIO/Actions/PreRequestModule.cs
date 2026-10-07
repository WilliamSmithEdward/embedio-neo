using System.Threading.Tasks;
using EmbedIO.Utilities;

namespace EmbedIO.Actions
{
    /// <summary>
    /// Runs an application callback before later modules without implicitly handling the request.
    /// </summary>
    /// <remarks>
    /// Register before the modules it should precede. The callback may explicitly handle
    /// or reject a request; otherwise dispatch continues with existing request URL/data.
    /// </remarks>
    public sealed class PreRequestModule : WebModuleBase
    {
        private readonly RequestHandlerCallback _handler;

        /// <summary>Creates a callback module for all request paths.</summary>
        /// <param name="handler">The callback to await for each matching request.</param>
        /// <exception cref="System.ArgumentNullException">The callback is null.</exception>
        public PreRequestModule(RequestHandlerCallback handler) : this("/", handler) { }

        /// <summary>Creates a callback module limited to the specified base route.</summary>
        /// <param name="baseRoute">The base route, with existing case-sensitive matching.</param>
        /// <param name="handler">The callback to await for each matching request.</param>
        /// <exception cref="System.ArgumentNullException">The callback is null.</exception>
        public PreRequestModule(string baseRoute, RequestHandlerCallback handler) : base(baseRoute)
            => _handler = Validate.NotNull(nameof(handler), handler);

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <inheritdoc />
        protected override Task OnRequestAsync(IHttpContext context) => _handler(context);
    }
}
