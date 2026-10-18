using System;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using EmbedIO.Problems;

namespace EmbedIO
{
    /// <summary>Creates explicit responses for controllers and direct handlers.</summary>
    public static partial class HttpResults
    {
        /// <summary>Returns a 200 payload using the configured module serializer.</summary>
        public static OkResult<T> Ok<T>(T? value) => new(value);

        /// <summary>Returns an empty typed 404.</summary>
        public static NotFoundResult<T> NotFound<T>() => new();

        /// <summary>Returns an empty 404.</summary>
        public static EmptyHttpResult NotFound() => new(404);

        /// <summary>Returns an empty typed 400.</summary>
        public static BadRequestResult<T> BadRequest<T>() => new();

        /// <summary>Returns an empty typed 409.</summary>
        public static ConflictResult<T> Conflict<T>() => new();

        /// <summary>Returns an empty 204.</summary>
        public static NoContentResult NoContent() => new();

        /// <summary>Returns an empty final status, from 200 through 599.</summary>
        public static EmptyHttpResult Status(int statusCode) => new(statusCode);

        /// <summary>Returns an empty final status using the standard enumeration.</summary>
        public static EmptyHttpResult Status(HttpStatusCode statusCode) => Status((int)statusCode);

        /// <summary>Returns a 201 payload and an application-provided Location.</summary>
        public static CreatedResult<T> Created<T>(string location, T? value) => new(location, value);

        /// <summary>Returns a 201 payload with an escaped named-route Location.</summary>
        public static CreatedResult<T> CreatedAt<T>(
            Routing.NamedRoutes routes,
            string routeName,
            System.Collections.Generic.IReadOnlyDictionary<string, object?> values,
            T? value
        ) => Created((routes ?? throw new ArgumentNullException(nameof(routes))).GetPath(routeName, values), value);

        /// <summary>Returns a 202 payload and optional status-resource Location.</summary>
        public static AcceptedResult<T> Accepted<T>(T? value, string? location = null) => new(value, location);

        /// <summary>Returns UTF-8 text with an explicit media type and final body-bearing status.</summary>
        public static TextHttpResult Text(string value, string contentType = "text/plain", int statusCode = 200) =>
            new(value, contentType, statusCode);

        /// <summary>Returns UTF-8 text using the standard status enumeration.</summary>
        public static TextHttpResult Text(string value, string contentType, HttpStatusCode statusCode) =>
            Text(value, contentType, (int)statusCode);

        /// <summary>Returns a JSON payload using explicit metadata, suitable for Native AOT.</summary>
        public static JsonHttpResult<T> Json<T>(T? value, JsonTypeInfo<T> typeInfo) => new(value, typeInfo);

        /// <summary>Returns a defensive copy of RFC 9457 Problem Details without throwing an exception.</summary>
        public static ProblemHttpResult Problem(ProblemDetails problem) => new(problem);

        /// <summary>Returns an explicit HTTP(S) or root-relative redirect. Status must be 301, 302, 303, 307 or 308.</summary>
        public static RedirectHttpResult Redirect(string location, int statusCode = 302) => new(location, statusCode);

        /// <summary>Returns a redirect using the standard status enumeration.</summary>
        public static RedirectHttpResult Redirect(string location, HttpStatusCode statusCode) =>
            Redirect(location, (int)statusCode);

        internal static string ValidateLocation(string value)
        {
            value = global::EmbedIO.Utilities.Validate.NotNull(nameof(value), value);

            if (
                value.Length == 0
                || value.Length > 8192
                || value.StartsWith("//", StringComparison.Ordinal)
                || Internal.StringOperations.IndexOfOrdinal(value, '\\') >= 0
            )
                throw new ArgumentException("Use an HTTP(S) URL or root-relative path.", nameof(value));

            foreach (var c in value)
                if (c < 33 || c > 126)
                    throw new ArgumentException(
                        "Location must contain escaped ASCII without whitespace or controls.",
                        nameof(value)
                    );

            if (value[0] == '/') return value;

            if (
                !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.UserInfo.Length != 0
            )
                throw new ArgumentException(
                    "Use an HTTP(S) URL without credentials or a root-relative path.",
                    nameof(value)
                );

            return value;
        }

        internal static string MediaType(string value)
        {
            value = global::EmbedIO.Utilities.Validate.NotNull(nameof(value), value);

            if (value.Length > 1024) throw new ArgumentException("Media type is too long.", nameof(value));

            foreach (var c in value)
                if (c < 32 || c > 126) throw new ArgumentException("Media type must be visible ASCII.", nameof(value));

            if (
                !MediaTypeHeaderValue.TryParse(value, out var parsed)
                || Internal.StringOperations.IndexOfOrdinal(parsed.MediaType ?? string.Empty, '*') >= 0
            )
                throw new ArgumentException("Invalid media type.", nameof(value));

            return value;
        }

        internal static int BodyStatus(int status)
        {
            if (status < 200 || status > 599 || status == 204 || status == 205 || status == 304)
                throw new ArgumentOutOfRangeException(nameof(status));

            return status;
        }
    }

    /// <summary>An empty response with a final status.</summary>
    public sealed class EmptyHttpResult : HttpResult
    {
        internal EmptyHttpResult(int status) : base(status) { }

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer)
        {
            context.Response.ContentLength64 = 0;
            return Task.CompletedTask;
        }
    }

    /// <summary>An empty 204 response.</summary>
    public sealed class NoContentResult : HttpResult
    {
        internal NoContentResult() : base(204) { }

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer)
        {
            context.Response.ContentLength64 = 0;
            return Task.CompletedTask;
        }
    }

    /// <summary>A 200 response carrying the declared payload type.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    public sealed class OkResult<T> : HttpResult<T>
    {
        internal OkResult(T? value) : base(200, value, true) { }
    }

    /// <summary>A 404 response carrying the declared payload type.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    public sealed class NotFoundResult<T> : HttpResult<T>
    {
        internal NotFoundResult() : base(404, default, false) { }
    }

    /// <summary>A 201 response carrying the declared payload type.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    public sealed class CreatedResult<T> : HttpResult<T>
    {
        internal CreatedResult(string location, T? value) : base(201, value, true, location: location) { }
    }

    /// <summary>A 202 response carrying the declared payload type.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    public sealed class AcceptedResult<T> : HttpResult<T>
    {
        internal AcceptedResult(T? value, string? location) : base(202, value, true, location: location) { }
    }

    /// <summary>An empty typed 400 response.</summary>
    /// <typeparam name="T">The successful payload type.</typeparam>
    public sealed class BadRequestResult<T> : HttpResult<T>
    {
        internal BadRequestResult() : base(400, default, false) { }
    }

    /// <summary>An empty typed 409 response.</summary>
    /// <typeparam name="T">The successful payload type.</typeparam>
    public sealed class ConflictResult<T> : HttpResult<T>
    {
        internal ConflictResult() : base(409, default, false) { }
    }

    /// <summary>An explicit UTF-8 text response.</summary>
    public sealed class TextHttpResult : HttpResult
    {
        internal TextHttpResult(string value, string mediaType, int status)
            : base(HttpResults.BodyStatus(status), HttpResults.MediaType(mediaType)) =>
            Value = value ?? throw new ArgumentNullException(nameof(value));

        /// <summary>Gets the text payload.</summary>
        public string Value { get; }

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer)
        {
            context.Response.ContentEncoding = WebServer.Utf8NoBomEncoding;
            return ResponseSerializer.None(false)(context, Value);
        }
    }

    /// <summary>A JSON response with explicit serialization metadata.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    public sealed class JsonHttpResult<T> : HttpResult<T>
    {
        private readonly JsonTypeInfo _typeInfo;

        internal JsonHttpResult(T? value, JsonTypeInfo<T> typeInfo) : base(200, value, true, "application/json") =>
            _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer) =>
            ResponseSerializer.WriteJsonBytesAsync(
                context,
                JsonSerializer.SerializeToUtf8Bytes((object?)Value, _typeInfo),
                false
            );
    }

    /// <summary>An explicit RFC 9457 error response, copied at construction and again per execution.</summary>
    public sealed class ProblemHttpResult : HttpResult
    {
        private readonly ProblemDetails _problem;

        internal ProblemHttpResult(ProblemDetails problem)
            : base(
                (problem ?? throw new ArgumentNullException(nameof(problem))).Status,
                ProblemDetailsHandler.MediaType
            ) => _problem = problem.Copy();

        /// <summary>Gets a copy of the problem so callers cannot change future executions.</summary>
        public ProblemDetails Problem => _problem.Copy();

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer) =>
            context.SendProblemAsync(_problem);
    }

    /// <summary>A redirect with an explicit validated Location.</summary>
    public sealed class RedirectHttpResult : HttpResult
    {
        internal RedirectHttpResult(string location, int status) : base(ValidateStatus(status), location: location) { }

        private static int ValidateStatus(int status) =>
            status is 301 or 302 or 303 or 307 or 308 ? status : throw new ArgumentOutOfRangeException(nameof(status));

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer)
        {
            context.Response.ContentLength64 = 0;
            return Task.CompletedTask;
        }
    }
}
