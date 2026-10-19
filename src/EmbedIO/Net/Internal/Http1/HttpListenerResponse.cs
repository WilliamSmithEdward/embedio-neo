using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    // Independently authored response state. The output owner orders wire commits;
    // this application view owns mutable metadata and its once-only close claim.
    internal sealed class HttpListenerResponse : IHttpResponseSections, IDisposable
    {
        private static readonly string[] HandoffFramingFields = [
            HttpHeaderNames.ContentLength, HttpHeaderNames.TransferEncoding, HttpHeaderNames.KeepAlive,
            HttpHeaderNames.Connection, HttpHeaderNames.Upgrade
        ];
        private readonly HttpConnection _transport;
        private readonly HttpListenerRequest _request;
        private readonly string _identity;
        private int _closed;
        private int _status = 200;
        private string _mediaType = MimeType.Html;
        private bool _mediaTypeAssigned;
        private bool? _persistent;
        private bool _chunks;
        private bool _handoff;
        private bool _capsules;
        private CookieList? _cookies;
        private ResponseStream? _output;
        private HashSet<string>? _reservedTrailers;
        private byte[]? _trailerEnding;

        internal HttpListenerResponse(HttpListenerContext context)
        {
            _transport = context.Connection;
            _request = context.HttpListenerRequest;
            _identity = context.Id;
        }

        internal void CompleteRequestAbortObservation() => _transport.CompleteRequestAbortObservation(this);

        public WebHeaderCollection Headers { get; } = [];
        public Encoding? ContentEncoding { get; set; } = WebServer.DefaultEncoding;

        private string _statusDescription = "OK";

        // Refused where it is assigned, as System.Net.HttpListenerResponse does: this text goes
        // straight into the status line, so a control character would start a field of its own.
        public string StatusDescription
        {
            get => _statusDescription;
            set => _statusDescription = EmbedIO.Internal.HttpTokenSyntax.ValidatedReasonPhrase(value, nameof(value));
        }

        public Version ProtocolVersion =>
            _request.ProtocolVersion.Minor == 0 ? HttpVersion.Version10 : HttpVersion.Version11;
        public Stream OutputStream => _output ??= _transport.GetResponseStream();

        // Body bytes sent so far; zero when no body was written.
        internal long SentBodyBytes => _output?.SentBodyBytes ?? 0;
        public ICookieCollection Cookies => CookieCollection;
        internal CookieList? ExistingCookies => _cookies;

        internal CookieList CookieCollection
        {
            get => _cookies ??= [];
            set => _cookies = value;
        }

        internal bool HeadersSent { get; set; }
        internal bool IsHeadResponse => _request.HttpVerb == HttpVerbs.Head;
        internal bool IsTunnelResponse => _handoff;
        internal bool SuppressesBody => IsHeadResponse || _status < 200 || _status is 204 or 205 or 304;
        internal byte[]? EndingChunk => Volatile.Read(ref _trailerEnding);

        public long ContentLength64
        {
            get => long.TryParse(Headers[HttpHeaderNames.ContentLength], out var count) ? count : 0;
            set
            {
                RequireMutableHead();

                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Must be >= 0");

                if (_reservedTrailers != null)
                    throw new InvalidOperationException(
                        "Reserved HTTP/1 trailers require chunked framing without Content-Length."
                    );

                Headers[HttpHeaderNames.ContentLength] = value.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string ContentType
        {
            get => _mediaType;
            set
            {
                RequireMutableHead();
                _mediaType = Validate.NotNullOrEmpty(nameof(value), value);
                _mediaTypeAssigned = true;
            }
        }

        public bool KeepAlive
        {
            get => _persistent ?? _request.KeepAlive;
            set
            {
                RequireMutableHead();
                _persistent = value;
            }
        }

        public bool SendChunked
        {
            get => _chunks;
            set
            {
                RequireMutableHead();

                if (_reservedTrailers != null && !value)
                    throw new InvalidOperationException("Reserved HTTP/1 trailers require chunked framing.");

                _chunks = value;
            }
        }

        public int StatusCode
        {
            get => _status;
            set
            {
                RequireMutableHead();
                HttpResponseFieldSections.ValidateFinalStatus(value);

                if (_reservedTrailers != null && value is 204 or 205 or 304)
                    throw new InvalidOperationException("Reserved trailers require a body-capable final response.");

                _status = value;
                StatusDescription = HttpListenerResponseHelper.GetStatusDescription(value);
            }
        }

        internal void CommitWebSocketHandshake()
        {
            // The separate HTTP/1 handshake writer has committed a bodyless 101.
            // Publish that wire result without accepting 1xx through the public setter.
            _status = (int)HttpStatusCode.SwitchingProtocols;
            StatusDescription = HttpListenerResponseHelper.GetStatusDescription(_status);
            _chunks = false;
            HeadersSent = true;
            // The connection now carries the upgraded protocol, not further HTTP/1
            // messages (RFC 9110, Section 7.8). PrepareFraming never runs for this head,
            // so without this the request cycle would treat the connection as reusable
            // and read the upgraded protocol's bytes as a request head.
            _persistent = false;
        }

        private void RequireMutableHead()
        {
            if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(_identity);

            if (HeadersSent)
                throw new InvalidOperationException("Header values cannot be changed after headers are sent.");
        }

        void IDisposable.Dispose() => Finish(true, false);

        public void Close() => Finish(false, false);

        internal void Abort() => Finish(true, true);

        private void Finish(bool force, bool abort)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            if (abort) _transport.ForceClose();

            else _transport.Close(force);
        }

        public void SetCookie(Cookie cookie)
        {
            if (cookie == null) throw new ArgumentNullException(nameof(cookie));

            if (_cookies != null)
                foreach (var present in _cookies)
                    if (present.Name == cookie.Name && present.Domain == cookie.Domain && present.Path == cookie.Path)
                        throw new ArgumentException("The cookie already exists.");

            CookieCollection.Add(cookie);
        }

        public Task SendInformationalAsync(
            int statusCode,
            WebHeaderCollection headers,
            CancellationToken cancellationToken = default
        )
        {
            RequireMutableHead();

            if (ProtocolVersion < HttpVersion.Version11 || _handoff)
                throw new InvalidOperationException("This response cannot carry informational sections.");

            var fields = HttpResponseFieldSections.Informational(statusCode, headers);
            return ((ResponseStream)OutputStream).WriteInformationalAsync(
                HttpResponseFieldSections.Http1Informational(statusCode, fields),
                cancellationToken
            );
        }

        public void DeclareTrailers(params string[] fieldNames) => PrepareTrailers(fieldNames);

        internal void PrepareTrailers(string[] names)
        {
            RequireMutableHead();

            if (names == null) throw new ArgumentNullException(nameof(names));

            if (
                ProtocolVersion < HttpVersion.Version11
                || SuppressesBody
                || _handoff
                || (_request.HttpMethod == "CONNECT" && _status is >= 200 and < 300)
            )
                throw new InvalidOperationException("This response cannot carry trailers.");

            if (Headers[HttpHeaderNames.ContentLength] != null)
                throw new InvalidOperationException("HTTP/1 trailers require chunked framing without Content-Length.");

            var reserved = HttpResponseTrailerFields.Declaration(names);
            Headers["Trailer"] = string.Join(", ", reserved);
            _reservedTrailers = reserved;
            _chunks = true;
        }

        public void SetTrailers(WebHeaderCollection trailers)
        {
            if (trailers == null) throw new ArgumentNullException(nameof(trailers));

            if (_closed != 0 || _handoff || _reservedTrailers == null || !_chunks || SuppressesBody)
                throw new InvalidOperationException("Trailers were not reserved for an open response.");

            var snapshot = HttpResponseTrailerFields.Snapshot(trailers, _reservedTrailers);
            Volatile.Write(ref _trailerEnding, HttpResponseTrailerFields.ChunkEnd(snapshot));
        }

        internal void BeginTunnel(string? protocol, bool capsules)
        {
            RequireMutableHead();

            if (_reservedTrailers != null)
                throw new InvalidOperationException("A trailer response cannot become a tunnel.");

            _status = protocol == null ? 200 : 101;
            StatusDescription = HttpListenerResponseHelper.GetStatusDescription(_status);

            if (capsules)
            {
                HttpCapsuleProtocol.ValidateCarrierHeaders(_request.Headers);
                HttpCapsuleProtocol.ValidateCarrierHeaders(Headers, _status);

                if (_mediaTypeAssigned || _chunks)
                    throw new InvalidOperationException(
                        "Capsule carriers cannot configure representation type or chunked framing."
                    );

                Headers[HttpCapsuleProtocol.HeaderName] = "?1";
            }

            foreach (var field in HandoffFramingFields)
                Headers.Remove(field);

            if (protocol != null)
            {
                Headers[HttpHeaderNames.Connection] = "Upgrade";
                Headers[HttpHeaderNames.Upgrade] = protocol;
            }

            _chunks = false;
            _handoff = true;
            _capsules = capsules;
        }

        // Encodes the final head into a pooled array that also has room for the first body
        // segment of bodyCount bytes and, for a write that ends the body, its ending (see
        // ResponseStream.FirstSegmentReserve); the caller owns the lease and disposes it.
        internal Http1Head SendHeaders(bool closing, int bodyCount, bool ending = false)
        {
            if (
                _reservedTrailers != null
                && (!_chunks || SuppressesBody || _handoff || Headers[HttpHeaderNames.ContentLength] != null)
            )
                throw new InvalidOperationException("Reserved trailers require a body-capable chunked response.");

            if (_capsules) HttpCapsuleProtocol.ValidateCarrierHeaders(Headers, _status);

            PrepareRepresentation();

            if (!_handoff) PrepareFraming(closing);

            return WriteHeaders(bodyCount, ending);
        }

        private void PrepareRepresentation()
        {
            if (!_handoff || (_mediaTypeAssigned && !_capsules))
            {
                Headers.Add(
                    HttpHeaderNames.ContentType,
                    ContentEncoding != null
                        ? HttpResponseFieldSections.WithCharset(_mediaType, ContentEncoding.WebName)
                        : _mediaType
                );
            }

            if (Headers[HttpHeaderNames.Server] == null) Headers.Add(HttpHeaderNames.Server, WebServer.Signature);

            if (Headers[HttpHeaderNames.Date] == null)
                Headers.Add(HttpHeaderNames.Date, HttpResponseFieldSections.CurrentDate());
        }

        private bool HasDeclaredLength =>
            long.TryParse(
                Headers[HttpHeaderNames.ContentLength],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count
            )
                && count >= 0;

        private void PrepareFraming(bool closing)
        {
            if (_status < 200 || _status is 204 or 304)
            {
                if (_status != 304)
                {
                    Headers.Remove(HttpHeaderNames.ContentLength);
                    Headers.Remove(HttpHeaderNames.TransferEncoding);
                }

                _chunks = false;
            }

            else if (_status == 205)
            {
                // Reset Content has no representation content on any HTTP version.
                Headers[HttpHeaderNames.ContentLength] = "0";
                Headers.Remove(HttpHeaderNames.TransferEncoding);
                _chunks = false;
            }

            else if (closing && _reservedTrailers == null)
            {
                if (!IsHeadResponse && !HasDeclaredLength) Headers[HttpHeaderNames.ContentLength] = "0";

                _chunks = false;
            }

            else
            {
                if (ProtocolVersion < HttpVersion.Version11) _chunks = false;

                if (_chunks || !HasDeclaredLength)
                {
                    Headers.Remove(HttpHeaderNames.ContentLength);

                    if (ProtocolVersion >= HttpVersion.Version11) _chunks = true;
                }
            }

            if (_chunks) Headers.Add(HttpHeaderNames.TransferEncoding, "chunked");

            var persistent = _status is not 400 and not 408 and not 411 and not 413 and not 414 and not 500 and not 503
                && !_transport.IsDraining
                && KeepAlive;

            if (ProtocolVersion < HttpVersion.Version11 && !SuppressesBody && !HasDeclaredLength) persistent = false;

            if (ProtocolVersion == HttpVersion.Version11 && _request.HttpMethod == "CONNECT" && _status >= 300)
                persistent = false;

            // A body the server never invited with 100 Continue may still be on its way; the
            // connection closes after this response instead of waiting to drain it.
            if (_request.IsContinueOwed) persistent = false;

            _persistent = persistent;
            Headers.Add(HttpHeaderNames.Connection, persistent ? "keep-alive" : "close");

            if (persistent && ProtocolVersion >= HttpVersion.Version11)
                Headers.Add(HttpHeaderNames.KeepAlive, _transport.KeepAliveHeader);
        }

        private Http1Head WriteHeaders(int bodyCount, bool ending)
        {
            var encoded = Http1HeaderEncoding.Encode(this, bodyCount, ending);
            _output ??= _transport.GetResponseStream();
            HeadersSent = true;
            return encoded;
        }

        internal static void AppendCookieHeaderValue(
            StringBuilder target,
            Cookie cookie,
            IHttpResponse? response = null
        ) => ResponseCookieFields.Append(target, cookie, response);
    }
}
