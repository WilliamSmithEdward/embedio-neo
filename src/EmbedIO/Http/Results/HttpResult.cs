using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO
{
    /// <summary>An explicit HTTP response. Ordinary controller objects keep their existing serialization.</summary>
    public abstract class HttpResult
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>()
        );
        private int _claimed;
        private readonly bool _singleUse;

        internal HttpResult(
            int statusCode,
            string? contentType = null,
            string? location = null,
            string? disposition = null,
            bool singleUse = false,
            IReadOnlyDictionary<string, string>? sharedHeaders = null
        )
        {
            if (statusCode < 200 || statusCode > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));

            StatusCode = statusCode;
            ContentType = contentType;
            _singleUse = singleUse;

            if (location == null && disposition == null)
            {
                Headers = sharedHeaders ?? EmptyHeaders;
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (location != null) headers.Add("Location", HttpResults.ValidateLocation(location));

            if (disposition != null) headers.Add("Content-Disposition", disposition);

            Headers = new ReadOnlyDictionary<string, string>(headers);
        }

        /// <summary>Gets the initial status. Conditional file delivery may select 206, 304 or 412.</summary>
        public int StatusCode { get; }

        /// <summary>Gets the concrete selected response, including inside a typed alternative.</summary>
        public virtual HttpResult SelectedResult => this;

        /// <summary>Gets the explicit media type, or null when selected by the module serializer.</summary>
        public string? ContentType { get; }

        /// <summary>Gets immutable result headers. Transport and security policies may add or override fields.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>Executes this response through the current transport and awaits complete body delivery.</summary>
        /// <remarks>Pass the module serializer for payload results. Direct execution defaults to JSON.
        /// Stream results execute once; dispose an owned stream result if it is never executed.
        /// Cancellation follows the context token and transport errors, preserving existing request-abort opt-in.</remarks>
        public async Task ExecuteAsync(IHttpContext context, ResponseSerializerCallback? serializer = null)
        {
            if (_singleUse && Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
                throw new InvalidOperationException("A stream result can execute only once.");

            Exception? failure = null;

            try
            {
                context = Utilities.Validate.NotNull(nameof(context), context);
                context.CancellationToken.ThrowIfCancellationRequested();

                if (ResponseCommitment.IsCommitted(context))
                    throw new InvalidOperationException("The response is already committed.");

                context.Response.StatusCode = StatusCode;

                if (ContentType != null) context.Response.ContentType = ContentType;

                foreach (var header in Headers) context.Response.Headers[header.Key] = header.Value;

                await WriteAsync(context, serializer ?? ResponseSerializer.Default).ConfigureAwait(false);
            }

            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                failure = await AbortFailureAsync(context, error).ConfigureAwait(false);
            }

            try
            {
                Release();
            }

            catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                var combined = failure == null ? cleanup : new AggregateException(failure, cleanup);
                failure = await AbortFailureAsync(context, combined).ConfigureAwait(false);
            }

            if (failure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failure)
                    .Throw();
        }

        private static async Task<Exception> AbortFailureAsync(IHttpContext? context, Exception failure)
        {
            try
            {
                if (context != null && ResponseCommitment.IsCommitted(context))
                    await ResponseCommitment
                        .AbortAsync(context, failure)
                        .ConfigureAwait(false);
            }

            catch (Exception abort) when (abort is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                return new AggregateException(failure, abort);
            }

            return failure;
        }

        internal abstract Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer);

        internal virtual void Release() { }
    }

    /// <summary>A result carrying a declared payload type, including empty alternatives of that type.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    public abstract class HttpResult<T> : HttpResult
    {
        internal HttpResult(int status, T? value, bool hasValue, string? contentType = null, string? location = null)
            : base(status, contentType, location)
        {
            Value = value;
            HasValue = hasValue;
        }

        /// <summary>Gets the payload; use HasValue to distinguish an empty result from a null payload.</summary>
        public T? Value { get; }

        /// <summary>Gets whether a payload is present.</summary>
        public bool HasValue { get; }

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer)
        {
            if (HasValue) return serializer(context, Value);

            context.Response.ContentLength64 = 0;
            return Task.CompletedTask;
        }
    }

    /// <summary>A compile-time choice between two explicit response types. Declare concrete alternatives for OpenAPI.</summary>
    /// <typeparam name="TFirst">The first result type.</typeparam><typeparam name="TSecond">The second result type.</typeparam>
    public sealed class HttpResult<TFirst, TSecond> : HttpResult where TFirst : HttpResult where TSecond : HttpResult
    {
        private HttpResult(HttpResult result)
            : base(
                Utilities.Validate.NotNull(nameof(result), result).StatusCode,
                result.ContentType,
                sharedHeaders: result.Headers
            ) => Result = result;

        /// <summary>Gets the selected alternative for inspection in tests.</summary>
        public HttpResult Result { get; }

        /// <inheritdoc/>
        public override HttpResult SelectedResult => Result.SelectedResult;

        /// <summary>Wraps the first alternative.</summary>
        public static implicit operator HttpResult<TFirst, TSecond>(TFirst result) =>
            new(Utilities.Validate.NotNull(nameof(result), result));

        /// <summary>Wraps the second alternative.</summary>
        public static implicit operator HttpResult<TFirst, TSecond>(TSecond result) =>
            new(Utilities.Validate.NotNull(nameof(result), result));

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer) =>
            Result.ExecuteAsync(context, serializer);
    }

    /// <summary>A compile-time choice between three distinct concrete response types.</summary>
    /// <typeparam name="TFirst">The first alternative.</typeparam><typeparam name="TSecond">The second alternative.</typeparam><typeparam name="TThird">The third alternative.</typeparam>
    public sealed class HttpResult<TFirst, TSecond, TThird> : HttpResult
        where TFirst : HttpResult
        where TSecond : HttpResult
        where TThird : HttpResult
    {
        private HttpResult(HttpResult result)
            : base(
                Utilities.Validate.NotNull(nameof(result), result).StatusCode,
                result.ContentType,
                sharedHeaders: result.Headers
            ) => Result = result;

        /// <summary>Gets the selected alternative.</summary>
        public HttpResult Result { get; }

        /// <inheritdoc/>
        public override HttpResult SelectedResult => Result.SelectedResult;

        /// <summary>Wraps the first alternative.</summary>
        public static implicit operator HttpResult<TFirst, TSecond, TThird>(TFirst result) =>
            new(Utilities.Validate.NotNull(nameof(result), result));

        /// <summary>Wraps the second alternative.</summary>
        public static implicit operator HttpResult<TFirst, TSecond, TThird>(TSecond result) =>
            new(Utilities.Validate.NotNull(nameof(result), result));

        /// <summary>Wraps the third alternative.</summary>
        public static implicit operator HttpResult<TFirst, TSecond, TThird>(TThird result) =>
            new(Utilities.Validate.NotNull(nameof(result), result));

        internal override Task WriteAsync(IHttpContext context, ResponseSerializerCallback serializer) =>
            Result.ExecuteAsync(context, serializer);
    }
}
