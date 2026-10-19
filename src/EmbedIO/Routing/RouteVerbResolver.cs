using System;

namespace EmbedIO.Routing
{
    /// <summary>
    /// Handles a HTTP request by matching it against a route,
    /// possibly handling different HTTP methods via different handlers.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="RouteVerbResolver"/> class.
    /// </remarks>
    /// <param name="matcher">The <see cref="RouteMatcher"/> to match URL paths against.</param>
    /// <exception cref="ArgumentNullException">
    /// <para><paramref name="matcher"/> is <see langword="null"/>.</para>
    /// </exception>
    public sealed class RouteVerbResolver(RouteMatcher matcher) : RouteResolverBase<HttpVerbs>(matcher)
    {

        /// <inheritdoc />
        protected override HttpVerbs GetContextData(IHttpContext context) => context.Request.HttpVerb;

        /// <inheritdoc />
        protected override bool MatchContextData(HttpVerbs contextVerb, HttpVerbs handlerVerb) =>
            handlerVerb == HttpVerbs.Any || contextVerb == handlerVerb;
    }
}
