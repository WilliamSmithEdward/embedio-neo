using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace EmbedIO.Internal
{
    // Response-scoped metadata survives native SetCookie cloning without modifying Cookie.
    // Reference keys remain valid when callers mutate cookie values or scopes before commit.
    internal static class CookieSameSiteStore
    {
        private static readonly ConditionalWeakTable<IHttpResponse, Dictionary<Cookie, CookieSameSiteMode>> Values = new();

        internal static void ValidateMode(CookieSameSiteMode mode, string parameter)
        {
            if (mode != CookieSameSiteMode.None && mode != CookieSameSiteMode.Lax && mode != CookieSameSiteMode.Strict)
                throw new ArgumentOutOfRangeException(parameter);
        }

        internal static void ValidateCookie(Cookie cookie, CookieSameSiteMode mode)
        {
            if (mode == CookieSameSiteMode.None && !cookie.Secure)
                throw new ArgumentException("SameSite=None requires a Secure cookie.", nameof(cookie));
        }

        internal static void Attach(IHttpResponse response, Cookie cookie, CookieSameSiteMode mode)
        {
            var stored = response.Cookies.LastOrDefault(c => ReferenceEquals(c, cookie))
                ?? response.Cookies.Last(c => c.Equals(cookie));
            var values = Values.GetValue(response, _ => new Dictionary<Cookie, CookieSameSiteMode>(CookieReferences.Instance));
            lock (values) values[stored] = mode;
        }

        internal static CookieSameSiteMode? Get(IHttpResponse response, Cookie cookie)
        {
            if (!Values.TryGetValue(response, out var values)) return null;
            lock (values) return values.TryGetValue(cookie, out var mode) ? mode : null;
        }

        internal static void Append(StringBuilder value, IHttpResponse response, Cookie cookie)
        {
            var mode = Get(response, cookie);
            if (!mode.HasValue) return;
            ValidateCookie(cookie, mode.Value);
            value.Append("; SameSite=").Append(mode.Value.ToString());
        }

        private sealed class CookieReferences : IEqualityComparer<Cookie>
        {
            internal static readonly CookieReferences Instance = new();
            public bool Equals(Cookie? x, Cookie? y) => ReferenceEquals(x, y);
            public int GetHashCode(Cookie value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
