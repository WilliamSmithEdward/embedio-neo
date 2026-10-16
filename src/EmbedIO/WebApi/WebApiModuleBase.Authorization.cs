using System;
using System.Linq;
using System.Reflection;
using EmbedIO.Authorization;
using EmbedIO.Routing;

namespace EmbedIO.WebApi
{
    partial class WebApiModuleBase
    {
        private AuthorizationOptions _authorization = AuthorizationOptions.Empty;
        /// <summary>Configures named authorization policies before registering any controllers.</summary>
        /// <param name="options">Options copied at configuration time.</param>
        /// <remarks>Unconfigured authorization still enforces authentication and roles. Unknown policies fail registration.</remarks>
        public void ConfigureAuthorization(AuthorizationOptions options)
        {
            EnsureConfigurationNotLocked();
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (ControllerCount != 0) throw new InvalidOperationException("Configure authorization before registering controllers.");
            _authorization = options.Snapshot();
        }
        private static AuthorizationMetadata? ReadAuthorization(Type controllerType, MethodInfo method)
        {
            var attributes = controllerType.GetCustomAttributes(true).Concat(method.GetCustomAttributes(true)).ToArray();
            var allowAnonymous = attributes.OfType<AllowAnonymousAttribute>().Any();
            var requirements = attributes.OfType<AuthorizeAttribute>().Select(attribute => new AuthorizationRequirement(
                attribute.Policy, attribute.Roles == null ? Array.Empty<string>() : attribute.Roles.Split(',').Select(role => role.Trim()).ToArray())).ToArray();
            return !allowAnonymous && requirements.Length == 0 ? null : new AuthorizationMetadata(allowAnonymous, requirements);
        }
        private RouteHandlerCallback WrapAuthorization(RouteHandlerCallback handler, AuthorizationMetadata? metadata)
        {
            if (metadata == null || metadata.AllowAnonymous || metadata.Requirements.Count == 0) return handler;
            var options = _authorization;
            return async (context, route) =>
            {
                await options.AuthorizeAsync(context, metadata).ConfigureAwait(false);
                await handler(context, route).ConfigureAwait(false);
            };
        }
    }
}
