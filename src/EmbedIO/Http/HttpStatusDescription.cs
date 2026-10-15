using System.Net;
using EmbedIO.Net.Internal;

namespace EmbedIO
{
    /// <summary>
    /// <para>Provides standard HTTP status descriptions.</para>
    /// <para>Data contained in this class comes from the following sources:</para>
    /// <list type="bullet">
    /// <item><description><see href="https://tools.ietf.org/html/rfc7231#section-6">RFC7231 Section 6</see> (HTTP/1.1 Semantics and Content)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc6585">RFC6585</see> (Additional HTTP Status Codes)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc2774#section-7">RFC2774 Section 7</see> (An HTTP Extension Framework)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc7540#section-9.1.2">RFC7540 Section 9.1.2</see> (HTTP/2)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc4918#section-11">RFC4918 Section 11</see> (WebDAV)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc5842#section-7">RFC5842 Section 7</see> (Binding Extensions to WebDAV)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc7538#section-3">RFC7538 Section 3</see> (HTTP Status Code 308)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc3229#section-10.4.1">RFC3229 Section 10.4.1</see> (Delta encoding in HTTP)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc8297#section-2">RFC8297 Section 2</see> (Early Hints)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc7725#section-3">RFC7725 Section 3</see> (HTTP-status-451)</description></item>
    /// <item><description><see href="https://tools.ietf.org/html/rfc2295#section-8.1">RFC2295 Section 8.1</see> (Transparent Content Negotiation)</description></item>
    /// </list>
    /// </summary>
    public static class HttpStatusDescription
    {
        /// <summary>
        /// Attempts to get the standard status description for a <see cref="HttpStatusCode"/>.
        /// </summary>
        /// <param name="code">The HTTP status code for which the standard description
        /// is to be retrieved.</param>
        /// <param name="description">When this method returns, the standard HTTP status description
        /// for the specified <paramref name="code"/> if it was found, or <see langword="null"/>
        /// if it was not found. This parameter is passed uninitialized.</param>
        /// <returns><see langword="true"/> if the specified <paramref name="code"/> was found
        /// in the list of HTTP status codes for which the standard description is known;
        /// otherwise, <see langword="false"/>.</returns>
        /// <seealso cref="TryGet(int,out string)"/>
        /// <seealso cref="Get(HttpStatusCode)"/>
        public static bool TryGet(HttpStatusCode code, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? description) => TryGet((int)code, out description);

        /// <summary>
        /// Attempts to get the standard status description for a HTTP status code
        /// specified as an <see langword="int"/>.
        /// </summary>
        /// <param name="code">The HTTP status code for which the standard description
        /// is to be retrieved.</param>
        /// <param name="description">When this method returns, the standard HTTP status description
        /// for the specified <paramref name="code"/> if it was found, or <see langword="null"/>
        /// if it was not found. This parameter is passed uninitialized.</param>
        /// <returns><see langword="true"/> if the specified <paramref name="code"/> was found
        /// in the list of HTTP status codes for which the standard description is known;
        /// otherwise, <see langword="false"/>.</returns>
        /// <seealso cref="TryGet(HttpStatusCode,out string)"/>
        /// <seealso cref="Get(int)"/>
        public static bool TryGet(int code, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? description)
        {
            var phrase = HttpListenerResponseHelper.GetStatusDescription(code);
            description = phrase.Length == 0 ? null : phrase;
            return description != null;
        }

        /// <summary>
        /// Returns the standard status description for a <see cref="HttpStatusCode"/>.
        /// </summary>
        /// <param name="code">The HTTP status code for which the standard description
        /// is to be retrieved.</param>
        /// <returns>The standard HTTP status description for the specified <paramref name="code"/>
        /// if it was found, or <see langword="null"/> if it was not found.</returns>
        public static string? Get(HttpStatusCode code)
        {
            TryGet((int)code, out var description);
            return description;
        }

        /// <summary>
        /// Returns the standard status description for a HTTP status code
        /// specified as an <see langword="int"/>.
        /// </summary>
        /// <param name="code">The HTTP status code for which the standard description
        /// is to be retrieved.</param>
        /// <returns>The standard HTTP status description for the specified <paramref name="code"/>
        /// if it was found, or <see langword="null"/> if it was not found.</returns>
        public static string? Get(int code)
        {
            TryGet(code, out var description);
            return description;
        }
    }
}
