using System;
using System.Reflection;
using EmbedIO.Routing;
using EmbedIO.Security;

namespace EmbedIO.WebApi
{
    partial class WebApiModuleBase
    {
        private BrowserRequestProtection? _browserProtection;
        /// <summary>Enables browser protection for every registered controller route, before activation and binding.</summary>
        /// <param name="protection">Application-owned policy, configured before any controllers.</param>
        /// <remarks>Authentication/authorization run first. Attributes override token requirements only; host/origin
        /// checks remain. No policy is enabled by default. The module does not dispose the shared policy.</remarks>
        public void ConfigureBrowserProtection(BrowserRequestProtection protection)
        {
            EnsureConfigurationNotLocked();
            if (protection == null) throw new ArgumentNullException(nameof(protection));
            if (ControllerCount != 0) throw new InvalidOperationException("Configure browser protection before registering controllers.");
            _browserProtection = protection;
        }

        private static BrowserProtectionMode? ReadBrowserProtection(Type controller, MethodInfo method)
            => (method.GetCustomAttribute<BrowserProtectionAttribute>(true)
                ?? controller.GetCustomAttribute<BrowserProtectionAttribute>(true))?.Mode;

        private void ValidateBrowserProtection(BrowserProtectionMode? mode)
        {
            if (mode.HasValue && mode != BrowserProtectionMode.RequireToken && mode != BrowserProtectionMode.IgnoreToken)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (mode.HasValue && _browserProtection == null)
                throw new InvalidOperationException("Browser protection attributes require ConfigureBrowserProtection before registration.");
        }

        private RouteHandlerCallback WrapBrowserProtection(RouteHandlerCallback handler, BrowserProtectionMode? mode)
        {
            var protection = _browserProtection;
            if (protection == null) return handler;
            return (context, route) =>
            {
                protection.ValidateRequest(context, mode != BrowserProtectionMode.IgnoreToken);
                return handler(context, route);
            };
        }
    }
}
