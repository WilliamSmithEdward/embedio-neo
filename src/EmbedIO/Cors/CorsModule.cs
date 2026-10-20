using System;
using System.Linq;
using System.Threading.Tasks;
using EmbedIO.Utilities;

namespace EmbedIO.Cors
{
    /// <summary>
    /// Cross-origin resource sharing (CORS) control Module.
    /// CORS is a mechanism that allows restricted resources (e.g. fonts)
    /// on a web page to be requested from another domain outside the domain from which the resource originated.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="CorsModule" /> class.
    /// </remarks>
    /// <param name="baseRoute">The base route.</param>
    /// <param name="origins">The valid origins. The default is <see cref="All"/> (<c>*</c>).</param>
    /// <param name="headers">The valid headers. The default is <see cref="All"/> (<c>*</c>).</param>
    /// <param name="methods">The valid methods. The default is <see cref="All"/> (<c>*</c>).</param>
    /// <exception cref="ArgumentNullException">
    /// origins
    /// or
    /// headers
    /// or
    /// methods
    /// </exception>
    public class CorsModule(
        string baseRoute,
        string origins = CorsModule.All,
        string headers = CorsModule.All,
        string methods = CorsModule.All
    ) : WebModuleBase(baseRoute)
    {
        /// <summary>
        /// A string meaning "All" in CORS headers.
        /// </summary>
        public const string All = "*";
        private readonly string _origins = origins ?? throw new ArgumentNullException(nameof(origins));
        private readonly string _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        private readonly string _methods = methods ?? throw new ArgumentNullException(nameof(methods));
        private readonly string[] _validOrigins =
        [
            .. origins
                .ToUpperInvariant()
                .SplitByComma(StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
        ];
        private readonly string[] _validMethods =
        [
            .. methods
                .ToUpperInvariant()
                .SplitByComma(StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
        ];

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <inheritdoc />
        protected override Task OnRequestAsync(IHttpContext context)
        {
            if (context is null) throw new System.NullReferenceException();

            var isOptions = context.Request.HttpVerb == HttpVerbs.Options;

            // If we allow all we don't need to filter
            if (_origins == All && _headers == All && _methods == All)
            {
                context.Response.Headers.Set(HttpHeaderNames.AccessControlAllowOrigin, All);

                if (isOptions)
                {
                    ValidateHttpOptions(context);
                    context.SetHandled();
                }

                return Task.CompletedTask;
            }

            var currentOrigin = context.Request.Headers[HttpHeaderNames.Origin];

            // A restricted origin list makes the response depend on Origin; caches must key on it.
            if (_origins != All)
                context.Response.Headers.Add(HttpHeaderNames.Vary, HttpHeaderNames.Origin);

            if (string.IsNullOrWhiteSpace(currentOrigin) && context.Request.IsLocal)
                return Task.CompletedTask;

            if (_origins == All || _validOrigins.Contains(currentOrigin?.ToUpperInvariant()))
            {
                context.Response.Headers.Set(
                    HttpHeaderNames.AccessControlAllowOrigin,
                    _origins == All ? All : currentOrigin
                );

                if (isOptions)
                {
                    ValidateHttpOptions(context);
                    context.SetHandled();
                }
            }

            return Task.CompletedTask;
        }

        private void ValidateHttpOptions(IHttpContext context)
        {
            var requestHeadersHeader = context.Request.Headers[HttpHeaderNames.AccessControlRequestHeaders];

            if (!string.IsNullOrWhiteSpace(requestHeadersHeader))
            {
                // A restricted configuration advertises only its allowed header names.
                context.Response.Headers.Set(
                    HttpHeaderNames.AccessControlAllowHeaders,
                    _headers == All ? requestHeadersHeader : _headers
                );
            }

            var requestMethodHeader = context.Request.Headers[HttpHeaderNames.AccessControlRequestMethod];

            if (string.IsNullOrWhiteSpace(requestMethodHeader))
                return;

            var currentMethods = requestMethodHeader
                .ToUpperInvariant()
                .SplitByComma(StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());

            if (_methods != All && !currentMethods.Any(_validMethods.Contains))
                throw HttpException.BadRequest();

            context.Response.Headers.Set(HttpHeaderNames.AccessControlAllowMethods, requestMethodHeader);
        }
    }
}
