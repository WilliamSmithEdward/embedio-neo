using System;
using System.ComponentModel;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.WebApi
{
    /// <summary>Reflection-free operations used by generated request-object and field bindings.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class RequestBindingServices
    {
        /// <summary>Binds a query DTO.</summary>
        /// <typeparam name="T">DTO type.</typeparam>
        /// <param name="controller">Current controller.</param>
        /// <param name="plan">Cached plan.</param>
        /// <param name="name">Parameter name.</param>
        /// <returns>The bound DTO.</returns>
        public static T Query<T>(WebApiController controller, RequestObjectPlan<T> plan, string name) where T : class
        {
            controller = Validate.NotNull(nameof(controller), controller);

            plan = Validate.NotNull(nameof(plan), plan);

            return plan.Bind(BoundedRequestFields.Query(controller.HttpContext), name);
        }

        /// <summary>Binds an URL-encoded form DTO using existing form/body limits.</summary>
        /// <typeparam name="T">DTO type.</typeparam>
        /// <param name="controller">Current controller.</param>
        /// <param name="plan">Cached plan.</param>
        /// <param name="name">Parameter name.</param>
        /// <returns>The bound DTO.</returns>
        public static async Task<T> FormAsync<T>(WebApiController controller, RequestObjectPlan<T> plan, string name)
            where T : class
        {
            controller = Validate.NotNull(nameof(controller), controller);

            plan = Validate.NotNull(nameof(plan), plan);

            if (
                !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(
                    controller.HttpContext.Request.ContentType,
                    out var contentType
                )
                || !string.Equals(
                    contentType.MediaType,
                    "application/x-www-form-urlencoded",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new HttpException(415, "The request must use application/x-www-form-urlencoded.");

            return plan.Bind(
                await BoundedRequestFields
                    .FormAsync(controller.HttpContext)
                    .ConfigureAwait(false),
                name
            );
        }

        /// <summary>Parses a new explicit binding using invariant-culture converters.</summary>
        /// <typeparam name="T">Value type.</typeparam>
        /// <param name="value">Source value.</param>
        /// <param name="converter">Cached converter.</param>
        /// <returns>The parsed value.</returns>
        public static T Parse<T>(string value, TypeConverter? converter)
        {
            try
            {
                if (
                    StringConversion.TryConvert(
                        typeof(T),
                        value,
                        converter,
                        RequestFieldBinding.ConverterOrThrow,
                        out var parsed,
                        out _
                    )
                ) return parsed is T result ? result : throw new FormatException("The value is invalid.");
            }

            catch (Exception error) when (
                error.GetType() == typeof(Exception)
                && converter is BaseNumberConverter
                    && error.InnerException is FormatException
                    or OverflowException
                    or ArgumentException
                    or IndexOutOfRangeException
            )
            {
                throw new FormatException("The value is invalid.");
            }

            throw new FormatException("The value is invalid.");
        }

        /// <summary>Parses every repeated value without splitting commas.</summary>
        /// <typeparam name="T">Element type.</typeparam>
        /// <param name="values">Source values.</param>
        /// <param name="parse">Cached element parser.</param>
        /// <returns>Parsed values in source order.</returns>
        public static T[] Array<T>(string[] values, Func<string, T> parse)
        {
            values = Validate.NotNull(nameof(values), values);

            parse = Validate.NotNull(nameof(parse), parse);

            var result = new T[values.Length];

            for (var i = 0; i < values.Length; i++) result[i] = parse(values[i]);

            return result;
        }
    }
}
