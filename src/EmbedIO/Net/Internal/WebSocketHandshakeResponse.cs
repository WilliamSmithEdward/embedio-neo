using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace EmbedIO.Net.Internal
{
    internal class WebSocketHandshakeResponse
    {
        private const int HandshakeStatusCode = (int)HttpStatusCode.SwitchingProtocols;

        internal WebSocketHandshakeResponse(IHttpContext context)
        {
            ProtocolVersion = HttpVersion.Version11;
            Headers = context.Response.Headers;
            var rawCookies = Headers.GetValues(HttpHeaderNames.SetCookie) ?? Array.Empty<string>();
            Headers.Clear(); // Use only headers mentioned in RFC6455 - scrap all the rest.
            StatusCode = HandshakeStatusCode;
            Reason = HttpListenerResponseHelper.GetStatusDescription(HandshakeStatusCode);

            Headers[HttpHeaderNames.Upgrade] = "websocket";
            Headers[HttpHeaderNames.Connection] = "Upgrade";

            var configuredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var value = new StringBuilder();
            foreach (var cookie in context.Response.Cookies)
            {
                if (cookie.Name.Length == 0)
                    continue;
                configuredNames.Add(cookie.Name);
                value.Clear();
                HttpListenerResponse.AppendCookieHeaderValue(value, cookie, context.Response);
                Headers.Add(HttpHeaderNames.SetCookie, value.ToString());
            }

            foreach (var raw in rawCookies)
            {
                Headers.Add(HttpHeaderNames.SetCookie, raw);
                var equals = raw.IndexOf("=", System.StringComparison.Ordinal);
                if (equals > 0) configuredNames.Add(raw.Substring(0, equals).Trim());
            }

            // Retain legacy request-cookie echo unless an explicit response cookie replaces it.
            // Request headers do not contain the response cookie's scope or security attributes.
            foreach (var cookie in context.Request.Cookies)
            {
                if (!configuredNames.Contains(cookie.Name))
                    Headers.Add(HttpHeaderNames.SetCookie, cookie.ToString());
            }
        }

        public string Reason { get; }

        public int StatusCode { get; }

        public NameValueCollection Headers { get; }

        public Version ProtocolVersion { get; }

        public override string ToString()
        {
            var output = new StringBuilder(64)
                .AppendFormat(CultureInfo.InvariantCulture, "HTTP/{0} {1} {2}\r\n", ProtocolVersion, StatusCode, Reason);

            foreach (var key in Headers.AllKeys)
            {
                if (string.Equals(key, HttpHeaderNames.SetCookie, StringComparison.OrdinalIgnoreCase))
                {
                    // Set-Cookie is not a comma-separated list. Preserve each stored value intact.
                    foreach (var value in Headers.GetValues(key) ?? Array.Empty<string>())
                        _ = output.AppendFormat(CultureInfo.InvariantCulture, "{0}: {1}\r\n", key, value);
                }
                else
                {
                    _ = output.AppendFormat(CultureInfo.InvariantCulture, "{0}: {1}\r\n", key, Headers[key]);
                }
            }

            _ = output.Append("\r\n");

            return output.ToString();
        }
    }
}
