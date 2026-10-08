using System;
using System.Net;
using EmbedIO.Internal;
using EmbedIO.Net.Internal;
using EmbedIO.Utilities;

namespace EmbedIO
{
    /// <summary>
    /// Provides extension methods for types implementing <see cref="IHttpResponse"/>.
    /// </summary>
    public static class HttpResponseExtensions
    {
        /// <summary>Sets a cookie with an explicit SameSite policy on a built-in listener response.</summary>
        /// <param name="this">The response.</param>
        /// <param name="cookie">The cookie to set.</param>
        /// <param name="sameSite">The explicit policy; None requires Secure.</param>
        /// <remarks>Omit this overload to retain existing cookie behavior. Configure before headers commit.</remarks>
        /// <exception cref="ArgumentNullException">The response or cookie is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The policy is not defined.</exception>
        /// <exception cref="ArgumentException">None is selected without Secure, or existing cookie validation fails.</exception>
        /// <exception cref="NotSupportedException">The response uses a custom cookie serializer.</exception>
        public static void SetCookie(this IHttpResponse? @this, Cookie? cookie, CookieSameSiteMode sameSite)
        {
            if (@this == null) throw new ArgumentNullException(nameof(@this));
            if (cookie == null) throw new ArgumentNullException(nameof(cookie));
            CookieSameSiteStore.ValidateMode(sameSite, nameof(sameSite));
            CookieSameSiteStore.ValidateCookie(cookie, sameSite);
            if (@this is not EmbedIO.Net.Internal.HttpListenerResponse && @this is not SystemHttpResponse)
                throw new NotSupportedException("SameSite cookie metadata requires a built-in listener response.");
            @this.SetCookie(cookie);
            CookieSameSiteStore.Attach(@this, cookie, sameSite);
        }

        /// <summary>
        /// Sets the necessary headers to disable caching of a response on the client side.
        /// </summary>
        /// <param name="this">The <see cref="IHttpResponse"/> interface on which this method is called.</param>
        /// <exception cref="NullReferenceException"><paramref name="this"/> is <see langword="null"/>.</exception>
        public static void DisableCaching(this IHttpResponse @this)
        {
            if (@this is null) throw new System.NullReferenceException();
            var headers = @this.Headers;
            headers.Set(HttpHeaderNames.Expires, "Sat, 26 Jul 1997 05:00:00 GMT");
            headers.Set(HttpHeaderNames.LastModified, HttpDate.Format(DateTime.UtcNow));
            headers.Set(HttpHeaderNames.CacheControl, "no-store, no-cache, must-revalidate");
            headers.Add(HttpHeaderNames.Pragma, "no-cache");
        }

        /// <summary>
        /// Prepares a standard response without a body for the specified status code.
        /// </summary>
        /// <param name="this">The <see cref="IHttpResponse"/> interface on which this method is called.</param>
        /// <param name="statusCode">The HTTP status code of the response.</param>
        /// <exception cref="NullReferenceException"><paramref name="this"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">There is no standard status description for <paramref name="statusCode"/>.</exception>
        public static void SetEmptyResponse(this IHttpResponse @this, int statusCode)
        {
            if (@this is null) throw new System.NullReferenceException();
            if (!HttpStatusDescription.TryGet(statusCode, out var statusDescription))
                throw new ArgumentException("Status code has no standard description.", nameof(statusCode));

            @this.StatusCode = statusCode;
            @this.StatusDescription = statusDescription;
            @this.ContentType = MimeType.Default;
            @this.ContentEncoding = null;
        }
    }
}
