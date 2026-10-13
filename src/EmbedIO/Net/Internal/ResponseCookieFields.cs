using System;
using System.Net;
using System.Text;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    internal static class ResponseCookieFields
    {
        internal static void AppendLine(StringBuilder target, Cookie cookie, IHttpResponse response)
        {
            if (cookie.Name.Length == 0) return;
            target.Append("Set-Cookie: ");
            Append(target, cookie, response);
            target.Append("\r\n");
        }
        internal static void Append(StringBuilder target, Cookie cookie, IHttpResponse? response)
        {
            ResponseCookieFormatter.Append(target, cookie);
            if (response != null) CookieSameSiteStore.Append(target, response, cookie);
        }
    }
}
