using System;

namespace EmbedIO.Kestrel
{
    /// <summary>Explicit selection of the application-local Kestrel backend.</summary>
    public static class KestrelWebServerOptionsExtensions
    {
        /// <summary>Uses Kestrel for the selected TCP, HTTP/3 or combined hosting mode, retaining EmbedIO modules.</summary>
        /// <param name="options">The server options, before configuration is locked.</param>
        /// <returns>The same options.</returns>
        public static WebServerOptions WithKestrel(this WebServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.ListenerFactory = static configured => new KestrelListener(configured);
            return options;
        }
    }
}
