using System;
using System.Collections.Specialized;
using System.IO;

namespace EmbedIO.Net.Internal
{
    // Used only after an extension selects the capsule carrier. Ordinary HTTP
    // messages never acquire capsule semantics simply by passing this parser.
    internal static class HttpCapsuleProtocol
    {
        internal const string HeaderName = "Capsule-Protocol";
        internal static bool IsEnabled(NameValueCollection headers)
        {
            if (headers == null) throw new ArgumentNullException(nameof(headers));
            return HttpStructuredFields.TryParseBooleanItem(headers[HeaderName], out var enabled) && enabled;
        }
        internal static void ValidateCarrierHeaders(NameValueCollection headers, int? responseStatus = null)
        {
            if (headers == null) throw new ArgumentNullException(nameof(headers));
            if (headers[HttpHeaderNames.ContentLength] != null || headers[HttpHeaderNames.ContentType] != null
                || headers[HttpHeaderNames.TransferEncoding] != null)
                throw new InvalidDataException("Capsule carriers forbid Content-Length, Content-Type and Transfer-Encoding.");
            if (responseStatus.HasValue)
            {
                var status = responseStatus.Value;
                if ((status != 101 && (status < 200 || status >= 300)) || status == 204 || status == 205 || status == 206)
                    throw new InvalidDataException("This status cannot establish a capsule carrier.");
            }
        }
    }
}
