using System;
using EmbedIO.Authentication;

namespace EmbedIO
{
    partial class WebModuleContainerExtensions
    {
        /// <summary>
        /// Creates, configures and adds a Basic authentication module to a module container.
        /// </summary>
        /// <typeparam name="TContainer">The type of the module container.</typeparam>
        /// <param name="this">The module container.</param>
        /// <param name="baseRoute">The base route protected by the module, relative to its container.</param>
        /// <param name="configure">A callback invoked before the module is registered.</param>
        /// <param name="realm">The authentication realm; null or empty uses the normalized base route.</param>
        /// <returns>The same container, with the configured module added.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
        /// <remarks>
        /// Register this module before handlers it must protect. Basic credentials require trusted HTTPS
        /// outside controlled local demonstrations. Account storage and authentication behavior are
        /// those of <see cref="BasicAuthenticationModule"/>. If configuration throws, no module is added.
        /// </remarks>
        public static TContainer WithBasicAuthentication<TContainer>(this TContainer? @this,
            string baseRoute, Action<BasicAuthenticationModule>? configure, string? realm = null)
            where TContainer : class, IWebModuleContainer
        {
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            return WithModule(@this, new BasicAuthenticationModule(baseRoute, realm), configure);
        }
    }
}
