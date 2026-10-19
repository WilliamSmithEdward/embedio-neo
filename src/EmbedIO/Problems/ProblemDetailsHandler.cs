using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmbedIO.Utilities;

namespace EmbedIO.Problems
{
    /// <summary>
    /// Settings for <see cref="ProblemDetailsHandler"/>. The handlers copy them when created.
    /// </summary>
    public sealed class ProblemDetailsOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether an HTTP exception's message is sent as the problem's
        /// <c>detail</c>. The default is <see langword="true"/>: an HTTP exception's message is written
        /// for the client, as with EmbedIO's other handlers, and includes binding errors such as
        /// <c>Cannot convert field limit to Int32.</c>
        /// </summary>
        public bool IncludeHttpExceptionMessages { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether an unhandled exception's type, message and stack trace
        /// are sent. The default is <see langword="false"/>: the client learns only that the server
        /// failed, and the request ID to find the logged exception. Enable it only in development.
        /// </summary>
        public bool IncludeExceptionDetails { get; set; }

        /// <summary>
        /// Gets or sets a callback that adjusts each problem before it is sent, for example to set
        /// <see cref="ProblemDetails.Type"/> or add extensions. It receives a copy, so a problem thrown
        /// repeatedly is not changed.
        /// </summary>
        public Action<IHttpContext, ProblemDetails>? Customize { get; set; }

        /// <summary>
        /// Gets or sets the handler for HTTP exceptions of clients that prefer HTML to JSON, such as
        /// browsers. The default is <see cref="HttpExceptionHandler.HtmlResponse"/>; <see langword="null"/>
        /// sends problem details to every client.
        /// </summary>
        public HttpExceptionHandlerCallback? HtmlHttpExceptionHandler { get; set; } = HttpExceptionHandler.HtmlResponse;

        /// <summary>
        /// Gets or sets the handler for unhandled exceptions of clients that prefer HTML to JSON. The
        /// default is <see cref="ExceptionHandler.HtmlResponse"/>; <see langword="null"/> sends problem
        /// details to every client.
        /// </summary>
        public ExceptionHandlerCallback? HtmlExceptionHandler { get; set; } = ExceptionHandler.HtmlResponse;

        internal ProblemDetailsOptions Copy() => (ProblemDetailsOptions)MemberwiseClone();
    }

    /// <summary>
    /// <para>Exception handlers that answer with RFC 9457 problem details, <c>application/problem+json</c>.
    /// Install both with <see cref="WithProblemDetails{TWebServer}"/>, or set
    /// <see cref="IWebServer.OnHttpException"/> and <see cref="IWebServer.OnUnhandledException"/> (or a
    /// module's) individually.</para>
    /// <para>Every problem carries a <c>requestId</c> extension, the context ID that EmbedIO's log
    /// entries for the request carry, so a client's report can be matched to the server's log.</para>
    /// <para>Like EmbedIO's other handlers, these run only while the response can still be replaced:
    /// once its headers have been sent, EmbedIO aborts the response instead.</para>
    /// </summary>
    public static class ProblemDetailsHandler
    {
        /// <summary>The problem details media type.</summary>
        public const string MediaType = "application/problem+json";
        private const string UnexpectedError = "An unexpected error occurred.";

        /// <summary>
        /// Gets a handler that describes HTTP exceptions as problems. A <see cref="ProblemDetailsException"/>
        /// or an HTTP exception whose data object is a <see cref="ProblemDetails"/> is sent as is; any
        /// other becomes a problem with the exception's status and, by default, its message. Other data
        /// objects are not sent. Exceptions with a status below 400, such as redirects, get no body.
        /// </summary>
        /// <param name="options">The settings, or <see langword="null"/> for the defaults.</param>
        /// <returns>The handler.</returns>
        public static HttpExceptionHandlerCallback ForHttpExceptions(ProblemDetailsOptions? options = null)
        {
            var settings = (options ?? new ProblemDetailsOptions()).Copy();
            return (context, httpException) =>
            {
                if (context == null)
                    throw new ArgumentNullException(nameof(context));

                if (httpException == null)
                    throw new ArgumentNullException(nameof(httpException));

                var status = httpException.StatusCode;

                if (status < 400 || status > 599)
                    return Task.CompletedTask;

                if (settings.HtmlHttpExceptionHandler != null && !PrefersJson(context))
                    return settings.HtmlHttpExceptionHandler(context, httpException);

                var problem = httpException.DataObject is ProblemDetails carried && carried.Status == status
                    ? carried.Copy()
                    : new ProblemDetails(status)
                    {
                        Detail = settings.IncludeHttpExceptionMessages ? httpException.Message : null
                    };
                return SendAsync(context, problem, settings.Customize);
            };
        }

        /// <summary>
        /// Gets a handler that describes unhandled exceptions as a 500 problem. By default the problem says
        /// only that an unexpected error occurred; the exception is logged by EmbedIO with the request ID.
        /// </summary>
        /// <param name="options">The settings, or <see langword="null"/> for the defaults.</param>
        /// <returns>The handler.</returns>
        public static ExceptionHandlerCallback ForUnhandledExceptions(ProblemDetailsOptions? options = null)
        {
            var settings = (options ?? new ProblemDetailsOptions()).Copy();
            return (context, exception) =>
            {
                if (context == null)
                    throw new ArgumentNullException(nameof(context));

                if (exception == null)
                    throw new ArgumentNullException(nameof(exception));

                if (settings.HtmlExceptionHandler != null && !PrefersJson(context))
                    return settings.HtmlExceptionHandler(context, exception);

                var problem = new ProblemDetails(500)
                {
                    Detail = settings.IncludeExceptionDetails ? exception.Message : UnexpectedError
                };

                if (settings.IncludeExceptionDetails)
                {
                    problem.WithExtension("exception", new JsonObject
                    {
                        ["type"] = exception.GetType().FullName,
                        ["message"] = exception.Message,
                        ["stackTrace"] = exception.StackTrace
                    });
                }

                return SendAsync(context, problem, settings.Customize);
            };
        }

        /// <summary>
        /// Installs <see cref="ForHttpExceptions"/> and <see cref="ForUnhandledExceptions"/> as a web
        /// server's exception handlers.
        /// </summary>
        /// <typeparam name="TWebServer">The type of the web server.</typeparam>
        /// <param name="this">The web server.</param>
        /// <param name="options">The settings, or <see langword="null"/> for the defaults.</param>
        /// <returns>The web server.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="this"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The web server has already started.</exception>
        public static TWebServer WithProblemDetails<TWebServer>(
            this TWebServer @this,
            ProblemDetailsOptions? options = null
        ) where TWebServer : IWebServer
        {
            if (@this == null)
                throw new ArgumentNullException(nameof(@this));

            @this.OnHttpException = ForHttpExceptions(options);
            @this.OnUnhandledException = ForUnhandledExceptions(options);
            return @this;
        }

        /// <summary>
        /// Sends a problem as the response, for example from a handler that detects a problem without
        /// throwing. The response's status is set to the problem's. It adds the <c>requestId</c>
        /// extension unless the problem already has one.
        /// </summary>
        /// <param name="context">The request's context.</param>
        /// <param name="problem">The problem.</param>
        /// <returns>A task completing when the response has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="problem"/> is <see langword="null"/>.</exception>
        public static Task SendProblemAsync(this IHttpContext context, ProblemDetails problem) =>
            SendAsync(
                Validate.NotNull(nameof(context), context),
                Validate
                    .NotNull(nameof(problem), problem)
                    .Copy(),
                null
            );

        private static async Task SendAsync(
            IHttpContext context,
            ProblemDetails problem,
            Action<IHttpContext, ProblemDetails>? customize
        )
        {
            customize?.Invoke(context, problem);

            if (!problem.Extensions.ContainsKey("requestId"))
                problem.WithExtension("requestId", context.Id);

            var body = Encoding.UTF8.GetBytes(problem.ToJson());
            var response = context.Response;
            response.StatusCode = problem.Status;
            response.ContentType = MediaType;
            response.ContentLength64 = body.Length;
            using var stream = response.OutputStream;
            await stream
                .WriteAsync(body, 0, body.Length, context.CancellationToken)
                .ConfigureAwait(false);
        }

        // JSON unless the client asks for HTML more strongly than for any JSON media range.
        private static bool PrefersJson(IHttpContext context)
        {
            var values = context.Request.Headers.GetValues(HttpHeaderNames.Accept);

            if (values == null || values.Length == 0)
                return true;

            int json = 0, html = 0;

            foreach (var entry in new QValueList(false, values).QValues)
            {
                var weight = entry.Value.Weight;

                switch (entry.Key.ToUpperInvariant())
                {
                    case "APPLICATION/PROBLEM+JSON":
                    case "APPLICATION/JSON":
                    case "APPLICATION/*":
                    case "*/*":
                        json = Math.Max(json, weight);
                        break;

                    case "TEXT/HTML":
                        html = Math.Max(html, weight);
                        break;
                }
            }

            return json > 0 && json >= html;
        }
    }
}
