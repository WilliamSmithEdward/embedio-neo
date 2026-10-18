using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmbedIO.Idempotency
{
    /// <summary>An immutable finite response recorded before transport delivery.</summary>
    public sealed class IdempotencyResponse
    {
        private readonly byte[] _body;
        /// <summary>Creates a result, copying bytes and headers. Cookies and transport headers are forbidden.</summary>
        /// <param name="statusCode">Final status code.</param>
        /// <param name="body">Finite response bytes.</param>
        /// <param name="contentType">Content type, including any charset.</param>
        /// <param name="headers">End-to-end representation headers.</param>
        /// <param name="characterEncoding">Optional encoding name, copied as immutable metadata.</param>
        public IdempotencyResponse(int statusCode, byte[] body, string? contentType = null, IEnumerable<KeyValuePair<string, string>>? headers = null, string? characterEncoding = null)
        {
            if (statusCode < 200 || statusCode > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));
            if (body == null) throw new ArgumentNullException(nameof(body));
            if ((statusCode == 204 || statusCode == 304) && body.Length != 0) throw new ArgumentException("This status cannot carry response body bytes.", nameof(body));
            if (body.Length > 16 * 1024 * 1024) throw new ArgumentException("Response is too large.", nameof(body));
            if (contentType != null && (contentType.Length > 1024 || InvalidHeaderValue(contentType))) throw new ArgumentException("Invalid content type.", nameof(contentType));
            if (characterEncoding?.Length > 128) throw new ArgumentException("Encoding name is too long.", nameof(characterEncoding));
            if (characterEncoding != null) _ = Encoding.GetEncoding(characterEncoding);
            CharacterEncoding = characterEncoding;
            _body = (byte[])body.Clone(); StatusCode = statusCode; ContentType = contentType;
            var values = (headers ?? Array.Empty<KeyValuePair<string, string>>()).ToArray();
            if (values.Length > 64) throw new ArgumentException("Too many headers.", nameof(headers));
            long size = _body.Length + ((contentType?.Length ?? 0) + (characterEncoding?.Length ?? 0)) * 2L;
            foreach (var pair in values)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 128 || pair.Key.Any(c => !IdempotencyPolicy.HeaderToken(c))
                    || pair.Value == null || InvalidHeaderValue(pair.Value) || Forbidden(pair.Key))
                    throw new ArgumentException("Invalid or unsafe replay header.", nameof(headers));
                size += (pair.Key.Length + pair.Value.Length) * 2L;
            }
            if (size - _body.Length > 16 * 1024) throw new ArgumentException("Response metadata is too large.", nameof(headers));
            Headers = new ReadOnlyCollection<KeyValuePair<string, string>>(values); SizeBytes = size;
        }
        /// <summary>Gets the final status code.</summary>
        public int StatusCode { get; }
        /// <summary>Gets a defensive copy of the body.</summary>
        public byte[] Body => (byte[])_body.Clone();
        /// <summary>Gets the content type.</summary>
        public string? ContentType { get; }
        /// <summary>Gets the optional character encoding name.</summary>
        public string? CharacterEncoding { get; }
        /// <summary>Gets immutable representation headers.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }
        /// <summary>Gets accounted body and UTF-16 metadata bytes.</summary>
        public long SizeBytes { get; }
        private static readonly HashSet<string> ForbiddenHeaders = new HashSet<string>(new[] { "Set-Cookie", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade", "Trailer", "TE", "Date", "Server", "Proxy-Authenticate", "Proxy-Authorization", "WWW-Authenticate", "Authentication-Info", "Content-Type" }, StringComparer.OrdinalIgnoreCase);
        private static bool InvalidHeaderValue(string value) => value.Any(c => c < ' ' && c != '\t' || c == '\u007f');
        internal static bool Forbidden(string name) => ForbiddenHeaders.Contains(name);
        internal async Task ReplayAsync(IHttpContext context)
        {
            context.Response.StatusCode = StatusCode; if (ContentType != null) context.Response.ContentType = ContentType;
            context.Response.ContentEncoding = CharacterEncoding == null ? null : Encoding.GetEncoding(CharacterEncoding);
            foreach (var group in Headers.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Equals("Vary", StringComparison.OrdinalIgnoreCase))
                {
                    var existing = context.Response.Headers.GetValues("Vary") ?? Array.Empty<string>();
                    var vary = existing.Concat(group.Select(p => p.Value)).SelectMany(v => v.Split(',')).Select(v => v.Trim()).Where(v => v.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase);
                    context.Response.Headers["Vary"] = string.Join(", ", vary);
                }
                else
                {
                    context.Response.Headers.Remove(group.Key);
                    foreach (var pair in group) context.Response.Headers.Add(pair.Key, pair.Value);
                }
            }
            context.Response.ContentLength64 = _body.LongLength; context.Response.SendChunked = false;
            await context.Response.OutputStream.WriteAsync(_body, 0, _body.Length, context.CancellationToken).ConfigureAwait(false);
        }
    }
}
