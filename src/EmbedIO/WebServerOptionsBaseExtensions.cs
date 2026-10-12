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
        /// Sets the maximum number of request body bytes a handler may read.
        /// </summary>
        /// <typeparam name="TOptions">The options type.</typeparam>
        /// <param name="this">The options instance.</param>
        /// <param name="value">The maximum transferred body bytes, or null for existing unlimited behavior.</param>
        /// <returns>The options instance.</returns>
        /// <exception cref="NullReferenceException">The options instance is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The limit is negative.</exception>
        /// <exception cref="InvalidOperationException">The configuration is locked.</exception>
        public static TOptions WithMaximumRequestBodyBytes<TOptions>(this TOptions @this, long? value)
            where TOptions : WebServerOptionsBase
        {
            if (@this is null) throw new System.NullReferenceException();
            @this.MaximumRequestBodyBytes = value;
            return @this;
        }

        /// <summary>
        /// Limits how many requests the server handles at the same time and how many may wait.
        /// </summary>
        /// <typeparam name="TOptions">The options type.</typeparam>
        /// <param name="this">The options instance.</param>
        /// <param name="maximum">The maximum concurrent requests, or null for existing unlimited dispatch.</param>
        /// <param name="queueLimit">How many requests may wait for a slot before HTTP 503 is returned.</param>
        /// <returns>The options instance.</returns>
        /// <exception cref="NullReferenceException">The options instance is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximum"/> is less than one,
        /// or <paramref name="queueLimit"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">The configuration is locked.</exception>
        public static TOptions WithMaximumConcurrentRequests<TOptions>(this TOptions @this, int? maximum, int queueLimit = 0)
            where TOptions : WebServerOptionsBase
        {
            if (@this is null) throw new System.NullReferenceException();
            @this.MaximumConcurrentRequests = maximum;
            @this.RequestQueueLimit = queueLimit;
            return @this;
        }

        /// <summary>
        /// Sets how long a request may be handled before its context token is canceled.
        /// </summary>
        /// <typeparam name="TOptions">The options type.</typeparam>
        /// <param name="this">The options instance.</param>
        /// <param name="value">The deadline, or null for existing behavior without a deadline.</param>
        /// <returns>The options instance.</returns>
        /// <exception cref="NullReferenceException">The options instance is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The deadline is not positive or is too large.</exception>
        /// <exception cref="InvalidOperationException">The configuration is locked.</exception>
        public static TOptions WithRequestTimeout<TOptions>(this TOptions @this, TimeSpan? value)
            where TOptions : WebServerOptionsBase
        {
            if (@this is null) throw new System.NullReferenceException();
            @this.RequestTimeout = value;
            return @this;
        }

        /// <summary>
        /// Limits the total size and the number of a request's header fields.
        /// </summary>
        /// <typeparam name="TOptions">The options type.</typeparam>
        /// <param name="this">The options instance.</param>
        /// <param name="maximumBytes">The maximum summed name and value lengths, or null for no limit.</param>
        /// <param name="maximumCount">The maximum number of field lines, or null for no limit.</param>
        /// <returns>The options instance.</returns>
        /// <exception cref="NullReferenceException">The options instance is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A limit is less than one.</exception>
        /// <exception cref="InvalidOperationException">The configuration is locked.</exception>
        public static TOptions WithRequestHeaderLimits<TOptions>(this TOptions @this, int? maximumBytes, int? maximumCount = null)
            where TOptions : WebServerOptionsBase
        {
            if (@this is null) throw new System.NullReferenceException();
            @this.MaximumRequestHeaderBytes = maximumBytes;
            @this.MaximumRequestHeaderCount = maximumCount;
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
