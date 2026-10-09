using System;

namespace EmbedIO
{
    /// <summary>
    /// Provides extension methods for classes derived from <see cref="WebServerOptionsBase"/>.
    /// </summary>
    public static class WebServerOptionsBaseExtensions
    {
        /// <summary>
        /// Sets the decoded byte limit for compressed request-stream helpers.
        /// </summary>
        /// <typeparam name="TOptions">The options type.</typeparam>
        /// <param name="this">The options instance.</param>
        /// <param name="value">The maximum decoded bytes, or null for existing unlimited behavior.</param>
        /// <returns>The options instance.</returns>
        /// <exception cref="NullReferenceException">The options instance is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The limit is negative.</exception>
        /// <exception cref="InvalidOperationException">The configuration is locked.</exception>
        public static TOptions WithMaximumDecompressedRequestBodyBytes<TOptions>(this TOptions @this, long? value)
            where TOptions : WebServerOptionsBase
        {
            if (@this is null) throw new System.NullReferenceException();
            @this.MaximumDecompressedRequestBodyBytes = value;
            return @this;
        }

        /// <summary>
        /// Adds a URL prefix.
        /// </summary>
        /// <typeparam name="TOptions">The type of the object on which this method is called.</typeparam>
        /// <param name="this">The object on which this method is called.</param>
        /// <param name="value">If <see langword="true"/>, enable support for compressed request bodies.</param>
        /// <returns><paramref name="this"/> with its <see cref="WebServerOptionsBase.SupportCompressedRequests">SupportCompressedRequests</see>
        /// property set to <paramref name="value"/>.</returns>
        /// <exception cref="NullReferenceException"><paramref name="this"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The configuration of <paramref name="this"/> is locked.</exception>
        public static TOptions WithSupportCompressedRequests<TOptions>(this TOptions @this, bool value)
            where TOptions : WebServerOptionsBase
        {
            if (@this is null) throw new System.NullReferenceException();
            @this.SupportCompressedRequests = value;
            return @this;
        }
    }
}
