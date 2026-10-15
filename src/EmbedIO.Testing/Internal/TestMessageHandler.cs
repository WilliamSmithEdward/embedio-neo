using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Utilities;

namespace EmbedIO.Testing.Internal
{
    internal sealed partial class TestMessageHandler : HttpMessageHandler
    {
        private readonly IHttpContextHandler _handler;

        public TestMessageHandler(IHttpContextHandler handler)
        {
            _handler = Validate.NotNull(nameof(handler), handler);
            CookieContainer = new CookieContainer();
        }

        public CookieContainer CookieContainer { get; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request = Validate.NotNull(nameof(request), request);
            var uri = request.RequestUri ?? throw new System.ArgumentException("The request must have a URL.", nameof(request));
            var serverRequest = new TestRequest(request, CookieContainer.GetCookieHeader(uri));

            var context = new TestContext(serverRequest)
            {
                CancellationToken = cancellationToken,
                Route = RouteMatch.UnsafeFromRoot(UrlPath.Normalize(serverRequest.Url.AbsolutePath, false))

            };

            await _handler.HandleContextAsync(context).ConfigureAwait(false);
            // Cleanup may produce a response body even for a canceled request.
            // Preserve the client's cancellation contract after cleanup completes.
            cancellationToken.ThrowIfCancellationRequested();
            var serverResponse = context.TestResponse;
            foreach (var cookie in serverResponse.Headers.GetValues(HttpHeaderNames.SetCookie) ?? System.Array.Empty<string>())
                CookieContainer.SetCookies(serverRequest.Url, cookie);

            var response = new HttpResponseMessage((HttpStatusCode)serverResponse.StatusCode)
            {
                RequestMessage = request,
                Version = serverResponse.ProtocolVersion,
                ReasonPhrase = serverResponse.StatusDescription,
                Content = serverResponse.Body == null ? null : new ByteArrayContent(serverResponse.Body),
            };
            foreach (var key in serverResponse.Headers.AllKeys)
            {
                switch (GetResponseHeaderType(key))
                {
                    case ResponseHeaderType.Content:
                        response.Content?.Headers.Add(key, serverResponse.Headers.GetValues(key) ?? System.Array.Empty<string>());
                        break;
                    case ResponseHeaderType.Response:
                        response.Headers.Add(key, serverResponse.Headers.GetValues(key) ?? System.Array.Empty<string>());
                        break;
                }
            }

            foreach (var cookie in serverResponse.Cookies)
            {
                if (cookie.Name.Length == 0) continue;
                var value = new System.Text.StringBuilder();
                EmbedIO.Internal.ResponseCookieFormatter.Append(value, cookie);
                response.Headers.Add(HttpHeaderNames.SetCookie, value.ToString());
                CookieContainer.Add(serverRequest.Url, cookie);
            }
            if (response.Content != null && response.Content.Headers.ContentType == null && serverResponse.ContentType != null)
            {
                var contentType = MediaTypeHeaderValue.Parse(serverResponse.ContentType);
                if (contentType.CharSet == null && serverResponse.ContentEncoding != null)
                    contentType.CharSet = serverResponse.ContentEncoding.WebName;
                response.Content.Headers.ContentType = contentType;
            }

            return response;
        }

        // Not all headers are created equal in System.Net.Http.
        // If a header is a "content" header, adding it to a HttpResponseMessage directly
        // will cause an InvalidOperationException.
        // The list of known headers with their respective "header types"
        // is conveniently hidden in an internal class of System.Net.Http,
        // because nobody outside the .NET team will ever need them, right?
        // https://github.com/dotnet/corefx/blob/master/src/System.Net.Http/src/System/Net/Http/Headers/KnownHeaders.cs
        // Here are the "content" headers, extracted on 2019-07-06:
        private static readonly System.Collections.Generic.Dictionary<string, ResponseHeaderType> ResponseHeaderTypes = new(System.StringComparer.OrdinalIgnoreCase)
        {
            // Content-Length is set automatically and shall not be touched
            [HttpHeaderNames.ContentLength] = ResponseHeaderType.None,
            // These headers belong to Content
            [HttpHeaderNames.Allow] = ResponseHeaderType.Content,
            [HttpHeaderNames.ContentDisposition] = ResponseHeaderType.Content,
            [HttpHeaderNames.ContentEncoding] = ResponseHeaderType.Content,
            [HttpHeaderNames.ContentLanguage] = ResponseHeaderType.Content,
            [HttpHeaderNames.ContentLocation] = ResponseHeaderType.Content,
            [HttpHeaderNames.ContentMD5] = ResponseHeaderType.Content,
            [HttpHeaderNames.ContentRange] = ResponseHeaderType.Content,
            [HttpHeaderNames.ContentType] = ResponseHeaderType.Content,
            [HttpHeaderNames.Expires] = ResponseHeaderType.Content,
            [HttpHeaderNames.LastModified] = ResponseHeaderType.Content,
        };

        private static ResponseHeaderType GetResponseHeaderType(string name)
            => ResponseHeaderTypes.TryGetValue(name, out var type) ? type : ResponseHeaderType.Response;
    }
}
