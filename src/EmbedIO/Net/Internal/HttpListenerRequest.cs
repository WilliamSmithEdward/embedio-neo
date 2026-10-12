using System;
using System.Buffers;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    // Application view of one parsed HTTP/1 head. Framing facts belong to the
    // head owner; body access borrows the existing session's request transport.
    internal sealed class HttpListenerRequest : IHttpRequest, IRequestPolicyTarget
    {
        private readonly HttpConnection _connection;
        private readonly Http1RequestHead _head = new();
        private CookieList? _cookies;
        private NameValueCollection? _queryString;
        private Stream? _inputStream;
        private Stream? _applicationInput;
        private int _continueStarted;
        private Func<CancellationToken, Task>? _sendContinue;
        private long _bodyLimit = -1;
        private Action? _onBodyLimitExceeded;
        private int _headerFieldCount;
        private long _headerFieldBytes;
        private bool? _keepAlive;

        internal HttpListenerRequest(HttpListenerContext context) => _connection = context.Connection;
        public NameValueCollection Headers => _head.Fields;
        public string HttpMethod => _head.Method;
        public HttpVerbs HttpVerb { get; private set; }
        public string RawTarget => _head.Target;
        public Uri Url => _head.Address;
        public Version ProtocolVersion => _head.Version;
        public IPEndPoint LocalEndPoint => _connection.LocalEndPoint;
        public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;
        public bool IsSecureConnection => _connection.IsSecure;
        public bool IsLocal => LocalEndPoint.Address.Equals(RemoteEndPoint.Address);
        public bool IsAuthenticated => false;
        public string? ContentType => Headers[HttpHeaderNames.ContentType];
        public string? UserAgent => Headers[HttpHeaderNames.UserAgent];
        public string UserHostAddress => LocalEndPoint.ToString();
        public string? UserHostName => Headers[HttpHeaderNames.Host];
        public Uri? UrlReferrer { get; private set; }
        public string[] AcceptTypes { get; private set; } = Array.Empty<string>();
        public string[] UserLanguages { get; private set; } = Array.Empty<string>();
        public ICookieCollection Cookies => _cookies ??= new CookieList();
        public long ContentLength64 => _head.Framed ? _head.Length
            : long.TryParse(Headers[HttpHeaderNames.ContentLength], out var length) ? length : 0;
        public bool HasEntityBody => _head.Chunked || ContentLength64 > 0;
        public Stream InputStream => !HasEntityBody || (_bodyLimit < 0 && _sendContinue == null) ? RawInputStream
            : _applicationInput ??= CreateApplicationInput();
        private Stream CreateApplicationInput()
        {
            var input = RawInputStream;
            if (_sendContinue != null) input = new DeferredContinueStream(input, StartContinue);
            return _bodyLimit < 0 ? input
                : new LimitedRequestStream(input, _bodyLimit, LimitedRequestStream.TransferredLimitMessage, _onBodyLimitExceeded);
        }
        // Set by the connection instead of writing 100 Continue before dispatch.
        internal void DeferContinue(Func<CancellationToken, Task> send) => _sendContinue = send;
        // A deferred 100 Continue was never sent: the client may still be waiting to upload.
        internal bool IsContinueOwed => _sendContinue != null && Volatile.Read(ref _continueStarted) == 0;
        private Task StartContinue(CancellationToken cancellationToken)
        {
            Volatile.Write(ref _continueStarted, 1);
            return (_sendContinue ?? throw new InvalidOperationException("No 100 Continue is owed."))(cancellationToken);
        }
        int IRequestPolicyTarget.HeaderFieldCount => _headerFieldCount;
        long IRequestPolicyTarget.HeaderFieldBytes => _headerFieldBytes;
        // The transport body without the server's byte limit. Framing and handoff checks
        // inspect this stream; applications read InputStream.
        internal Stream RawInputStream => _inputStream ??= HasEntityBody
            ? _connection.GetRequestStream(ContentLength64, _head.Chunked) : Stream.Null;
        void IRequestPolicyTarget.LimitBody(long maximumBytes, Action onExceeded)
        {
            if (_applicationInput != null) throw new InvalidOperationException("The request body was already opened.");
            _bodyLimit = maximumBytes;
            _onBodyLimitExceeded = onExceeded;
        }
        internal bool HasBodyFramingFailure => _inputStream is RequestStream body && body.HasBodyFramingFailure;
        internal bool IsBodyFramingError(Exception error) => _inputStream is RequestStream body && body.IsFramingError(error);
        internal bool RequiresContinue => ProtocolVersion >= HttpVersion.Version11 && HasEntityBody
            && HttpExpectations.ContainsContinue(Headers["Expect"]);
        public bool IsWebSocketRequest => HttpVerb == HttpVerbs.Get && ProtocolVersion >= HttpVersion.Version11
            && Headers.Contains(HttpHeaderNames.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
            && Headers.Contains(HttpHeaderNames.Connection, "Upgrade", StringComparison.OrdinalIgnoreCase);
        public bool KeepAlive => _keepAlive ??= !Headers.Contains(HttpHeaderNames.Connection, "close", StringComparison.OrdinalIgnoreCase)
            && (ProtocolVersion >= HttpVersion.Version11 || Headers.Contains(HttpHeaderNames.Connection, "keep-alive", StringComparison.OrdinalIgnoreCase));
        public Encoding ContentEncoding
        {
            get
            {
                var type = ContentType;
                if (!HasEntityBody || type == null) return WebServer.DefaultEncoding;
                var charset = HeaderUtility.GetCharset(type);
                if (string.IsNullOrEmpty(charset)) return WebServer.DefaultEncoding;
                try { return Encoding.GetEncoding(charset); }
                catch (ArgumentException) { return WebServer.DefaultEncoding; }
            }
        }
        public NameValueCollection QueryString
        {
            get
            {
                var current = Volatile.Read(ref _queryString);
                if (current != null) return current;
                var created = new NameValueCollection();
                return Interlocked.CompareExchange(ref _queryString, created, null) ?? created;
            }
        }
        internal void SetRequestLine(string line)
        {
            var error = _head.ReadLine(line);
            if (error != null) { _connection.SetError(error); return; }
            HttpVerb = IsKnownHttpMethod(HttpMethod, out var method) ? method : HttpVerbs.Any;
        }
        internal void AddHeader(string line)
        {
            var error = _head.ReadField(line, out var name, out var value);
            if (error != null) { _connection.SetError(error); return; }
            _headerFieldCount++;
            _headerFieldBytes += name.Length + value.Length;
            if (name.Equals("Accept", StringComparison.OrdinalIgnoreCase)) AcceptTypes = value.SplitByComma();
            else if (name.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)) UserLanguages = value.SplitByComma();
            else if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) _cookies = ParseCookies(value);
            else if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                try { UrlReferrer = new Uri(value); }
                catch (UriFormatException) { UrlReferrer = null; }
            }
        }
        internal void FinishInitialization()
        {
            var error = _head.Complete(LocalEndPoint, IsSecureConnection);
            if (error != null) { _connection.SetError(error); return; }
            var query = Url.Query;
            if (query.Length == 0) return;
            for (var start = query[0] == '?' ? 1 : 0; ;)
            {
                var end = query.IndexOf('&', start);
                if (end < 0) end = query.Length;
                var separator = query.IndexOf('=', start, end - start);
                if (separator < 0) QueryString.Add(null, WebUtility.UrlDecode(query.Substring(start, end - start)));
                else QueryString.Add(WebUtility.UrlDecode(query.Substring(start, separator - start)),
                    WebUtility.UrlDecode(query.Substring(separator + 1, end - separator - 1)));
                if (end == query.Length) return;
                start = end + 1;
            }
        }
        internal bool FlushInput()
        {
            if (!HasEntityBody) return true;
            // The client was never invited to upload: close instead of waiting for its body.
            if (IsContinueOwed) return false;
            Stream input;
            try { input = InputStream; }
            catch (ObjectDisposedException) { _inputStream = null; return true; }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return false; }
            if (input is RequestStream body && body.IsBodyConsumed) return true;
            var count = ContentLength64 > 0 ? (int)Math.Min(ContentLength64, 2048) : 2048;
            var buffer = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                while (input.Read(buffer, 0, count) != 0) { }
                return true;
            }
            catch (ObjectDisposedException) { _inputStream = null; return true; }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return false; }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }
        internal async Task<bool> FlushInputAsync()
        {
            if (!HasEntityBody) return true;
            // The client was never invited to upload: close instead of waiting for its body.
            if (IsContinueOwed) return false;
            var input = InputStream;
            if (input is RequestStream body && body.IsBodyConsumed) return true;
            var buffer = ArrayPool<byte>.Shared.Rent(2048);
            try
            {
                while (await input.ReadAsync(buffer, 0, 2048, CancellationToken.None).ConfigureAwait(false) != 0) { }
                return input is not RequestStream remaining || remaining.IsBodyConsumed;
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return false; }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }
        internal static CookieList ParseCookies(string value) => RequestCookieFields.Read(value);
        internal static bool IsKnownHttpMethod(string method, out HttpVerbs verb)
        {
            verb = method switch
            {
                "GET" => HttpVerbs.Get,
                "HEAD" => HttpVerbs.Head,
                "POST" => HttpVerbs.Post,
                "PUT" => HttpVerbs.Put,
                "DELETE" => HttpVerbs.Delete,
                "PATCH" => HttpVerbs.Patch,
                "OPTIONS" => HttpVerbs.Options,
                "QUERY" => HttpVerbs.Query,
                _ => HttpVerbs.Any,
            };
            return verb != HttpVerbs.Any;
        }
    }
}
