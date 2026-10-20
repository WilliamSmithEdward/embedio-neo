using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Health
{
    /// <summary>Optional GET/HEAD live and ready endpoints beneath an explicit module base route.</summary>
    /// <remarks>Place after application authentication/authorization and before catch-all modules.
    /// This module does not own the service. Detailed output requires an explicit authorization callback.</remarks>
    public sealed class HealthCheckModule : WebModuleBase
    {
        private readonly HealthCheckService _service;
        private readonly bool _includeDetails;
        private readonly Func<IHttpContext, Task<bool>>? _authorize;

        /// <summary>Creates optional health endpoints; no endpoint is installed by constructing a service alone.</summary>
        /// <param name="service">The health service for this host.</param>
        /// <param name="baseRoute">Base route, followed by live or ready (for example /health/ready).</param>
        /// <param name="includeDetails">Whether to include names and descriptions in JSON; false by default.</param>
        /// <param name="authorize">Optional access check for both endpoints. Mandatory for detailed output; failures deny access.</param>
        public HealthCheckModule(
            HealthCheckService service,
            string baseRoute = "/health/",
            bool includeDetails = false,
            Func<IHttpContext, Task<bool>>? authorize = null
        ) : base(baseRoute)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));

            if (includeDetails && authorize == null)
                throw new ArgumentException(
                    "Detailed health output requires explicit authorization.",
                    nameof(authorize)
                );

            _includeDetails = includeDetails;
            _authorize = authorize;
        }

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <inheritdoc />
        protected override async Task OnRequestAsync(IHttpContext context)
        {
            var path = context.Request.Url.AbsolutePath;
            var live = string.Equals(path, BaseRoute + "live", StringComparison.Ordinal);

            if (!live && !string.Equals(path, BaseRoute + "ready", StringComparison.Ordinal)) return;

            context.CancellationToken.ThrowIfCancellationRequested();
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Pragma"] = "no-cache";
            var allowed = _authorize == null;

            if (_authorize != null)
            {
                try
                {
                    allowed = await _authorize(context).ConfigureAwait(false);
                }

                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    allowed = false;
                }
            }

            context.CancellationToken.ThrowIfCancellationRequested();

            if (!allowed)
            {
                context.Response.StatusCode = 403;
                context.SetHandled();
                return;
            }

            if (context.Request.HttpVerb != HttpVerbs.Get && context.Request.HttpVerb != HttpVerbs.Head)
            {
                context.Response.StatusCode = 405;
                context.Response.Headers["Allow"] = "GET, HEAD";
                context.SetHandled();
                return;
            }

            var report = live
                ? null
                : await _service
                    .CheckReadinessAsync(context.CancellationToken)
                    .ConfigureAwait(false);
            var status = live
                ? _service.CheckLiveness().Status
                : (report ?? throw new InvalidOperationException()).Status;
            var body = _includeDetails ? Details(status, report) : Encoding.UTF8.GetBytes(status.ToString());
            context.Response.StatusCode = status == HealthStatus.Healthy ? 200 : 503;
            context.Response.ContentType = _includeDetails
                ? "application/json; charset=utf-8"
                : "text/plain; charset=utf-8";
            context.Response.ContentLength64 = body.Length;

            if (context.Request.HttpVerb != HttpVerbs.Head)
                await context.Response.OutputStream
                    .WriteAsync(body, 0, body.Length, context.CancellationToken)
                    .ConfigureAwait(false);

            context.SetHandled();
        }

        private static byte[] Details(HealthStatus status, HealthReport? report)
        {
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("status", status.ToString());
                writer.WriteStartObject("checks");

                if (report != null)
                    foreach (var entry in report.Checks)
                    {
                        writer.WriteStartObject(entry.Key);
                        writer.WriteString("status", entry.Value.Status.ToString());

                        if (entry.Value.Description != null) writer.WriteString("description", entry.Value.Description);

                        writer.WriteEndObject();
                    }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
    }
}
