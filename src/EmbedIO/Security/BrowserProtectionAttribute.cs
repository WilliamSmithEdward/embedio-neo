using System;

namespace EmbedIO.Security
{
    /// <summary>Selects whether unsafe controller actions require an anti-forgery token.</summary>
    public enum BrowserProtectionMode
    {
        /// <summary>Requires tokens for unsafe methods, in addition to host/origin checks.</summary>
        RequireToken = 0,
        /// <summary>Explicit token exemption. Host/origin checks still apply. Supply independent authentication.</summary>
        IgnoreToken = 1
    }

    /// <summary>Overrides the configured module's token requirement per class or action.</summary>
    /// <remarks>A method overrides an inherited class declaration. Configuration is required before registration.
    /// AllowAnonymous and before-handler hook selections do not exempt browser protection.</remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public sealed class BrowserProtectionAttribute : Attribute
    {
        /// <summary>Creates a declaration with the selected token requirement.</summary>
        /// <param name="mode">RequireToken by default; IgnoreToken is an explicit exemption.</param>
        public BrowserProtectionAttribute(BrowserProtectionMode mode = BrowserProtectionMode.RequireToken)
        {
            if (mode != BrowserProtectionMode.RequireToken && mode != BrowserProtectionMode.IgnoreToken)
                throw new ArgumentOutOfRangeException(nameof(mode));

            Mode = mode;
        }

        /// <summary>Gets the selected token requirement.</summary>
        public BrowserProtectionMode Mode { get; }
    }
}
