using System;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Routing;
using EmbedIO.Serialization;

namespace EmbedIO.WebApi
{
    /// <summary>
    /// Binding operations called by code that the EmbedIO-Neo source generator produces for
    /// <see cref="WebApiControllerBinding{TController}"/>. Each method reproduces what the
    /// reflection-based controller registration does for the same parameter, without reflection.
    /// This type supports generated code and is not intended to be used directly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class ControllerBindingServices
    {
        /// <summary>
        /// Gets the value of a route parameter, if the matched route has one with the specified name.
        /// </summary>
        /// <param name="route">The matched route.</param>
        /// <param name="name">The name of the controller method's parameter.</param>
        /// <param name="value">When this method returns <see langword="true"/>, the parameter's value.</param>
        /// <returns><see langword="true"/> if the route has a parameter named <paramref name="name"/>.</returns>
        public static bool TryGetRouteValue(RouteMatch route, string name, out string value)
        {
            route = route ?? throw new ArgumentNullException(nameof(route));
            var index = route.IndexOf(name);
            value = index < 0 ? string.Empty : route[index];
            return index >= 0;
        }

        /// <summary>
        /// Converts a route parameter's value to the type of a controller method's parameter.
        /// </summary>
        /// <typeparam name="T">The parameter's type.</typeparam>
        /// <param name="value">The route parameter's value.</param>
        /// <param name="converter">The type converter for <typeparamref name="T"/> (or its underlying type),
        /// or <see langword="null"/> for strings and enumerations.</param>
        /// <returns>The converted value.</returns>
        /// <exception cref="HttpException">The value is not valid for <typeparamref name="T"/> (400 Bad Request).</exception>
        /// <exception cref="FormatException">The value cannot be converted.</exception>
        public static T? ConvertRouteValue<T>(string? value, TypeConverter? converter)
        {
            bool invalidValue;
            try
            {
                if (StringConversion.TryConvert(typeof(T), value, converter, RequestFieldBinding.ConverterOrThrow, out var result, out invalidValue))
                    return (T?)result;
            }
            catch (Exception exception) when (!string.IsNullOrEmpty(value)
                && exception.GetType() == typeof(Exception)
                && converter is BaseNumberConverter
                && (exception.InnerException is ArgumentException || exception.InnerException is FormatException
                    || exception.InnerException is OverflowException || exception.InnerException is IndexOutOfRangeException))
            {
                // BaseNumberConverter wraps parsing failures in a plain Exception, which the
                // reflection binder translates at the route boundary; see FromString.ConvertTo.
                throw HttpException.BadRequest("Invalid route parameter value.");
            }

            if (invalidValue) throw HttpException.BadRequest("Invalid route parameter value.");
            throw new FormatException($"Cannot convert '{value}' to {typeof(T).FullName}.");
        }

        /// <summary>Binds a <see cref="QueryFieldAttribute"/> parameter of type <see cref="string"/>.</summary>
        /// <param name="controller">The controller.</param>
        /// <param name="fieldName">The field name.</param>
        /// <param name="badRequestIfMissing">The attribute's <see cref="QueryFieldAttribute.BadRequestIfMissing"/>.</param>
        /// <returns>The field's last value, or <see langword="null"/>.</returns>
        public static string? QueryFieldString(WebApiController controller, string fieldName, bool badRequestIfMissing)
            => RequestFieldBinding.GetString(QueryData(controller), fieldName, badRequestIfMissing, RequestFieldBinding.Query);

        /// <summary>Binds a <see cref="QueryFieldAttribute"/> parameter of type <see cref="string"/> array.</summary>
        /// <param name="controller">The controller.</param>
        /// <param name="fieldName">The field name.</param>
        /// <param name="badRequestIfMissing">The attribute's <see cref="QueryFieldAttribute.BadRequestIfMissing"/>.</param>
        /// <returns>The field's values.</returns>
        public static string[] QueryFieldStrings(WebApiController controller, string fieldName, bool badRequestIfMissing)
            => RequestFieldBinding.GetStrings(QueryData(controller), fieldName, badRequestIfMissing, RequestFieldBinding.Query);

        /// <summary>Binds a <see cref="QueryFieldAttribute"/> parameter of a converted type.</summary>
        /// <typeparam name="T">The parameter's type.</typeparam>
        /// <param name="controller">The controller.</param>
        /// <param name="fieldName">The field name.</param>
        /// <param name="badRequestIfMissing">The attribute's <see cref="QueryFieldAttribute.BadRequestIfMissing"/>.</param>
        /// <param name="parameterName">The parameter's name.</param>
        /// <param name="converter">The type converter, or <see langword="null"/> for strings and enumerations.</param>
        /// <returns>The converted last value, or the default value if the field is absent.</returns>
        public static T? QueryField<T>(WebApiController controller, string fieldName, bool badRequestIfMissing, string parameterName, TypeConverter? converter)
            => RequestFieldBinding.Get<T>(QueryData(controller), fieldName, badRequestIfMissing, RequestFieldBinding.Query, parameterName, converter);

        /// <summary>Binds a <see cref="QueryFieldAttribute"/> parameter of a converted array type.</summary>
        /// <typeparam name="TElement">The array's element type.</typeparam>
        /// <param name="controller">The controller.</param>
        /// <param name="fieldName">The field name.</param>
        /// <param name="badRequestIfMissing">The attribute's <see cref="QueryFieldAttribute.BadRequestIfMissing"/>.</param>
        /// <param name="converter">The type converter, or <see langword="null"/> for strings and enumerations.</param>
        /// <returns>The converted values.</returns>
        public static TElement?[] QueryFieldArray<TElement>(WebApiController controller, string fieldName, bool badRequestIfMissing, TypeConverter? converter)
            => RequestFieldBinding.GetArray<TElement>(QueryData(controller), fieldName, badRequestIfMissing, RequestFieldBinding.Query, converter);

        /// <summary>Binds a <see cref="FormFieldAttribute"/> parameter of type <see cref="string"/>.</summary>
        /// <param name="controller">The controller.</param>
        /// <param name="fieldName">The field name.</param>
        /// <param name="badRequestIfMissing">The attribute's <see cref="FormFieldAttribute.BadRequestIfMissing"/>.</param>
        /// <returns>A task whose result is the field's last value, or <see langword="null"/>.</returns>
        public static async Task<string?> FormFieldStringAsync(WebApiController controller, string fieldName, bool badRequestIfMissing)
            => RequestFieldBinding.GetString(await FormDataAsync(controller).ConfigureAwait(false), fieldName, badRequestIfMissing, RequestFieldBinding.Form);

        /// <summary>Binds a <see cref="FormFieldAttribute"/> parameter of type <see cref="string"/> array.</summary>
        /// <param name="controller">The controller.</param>
        /// <param name="fieldName">The field name.</param>
        /// <param name="badRequestIfMissing">The attribute's <see cref="FormFieldAttribute.BadRequestIfMissing"/>.</param>
        /// <returns>A task whose result is the field's values.</returns>
        public static async Task<string[]> FormFieldStringsAsync(WebApiController controller, string fieldName, bool badRequestIfMissing)
            => RequestFieldBinding.GetStrings(await FormDataAsync(controller).ConfigureAwait(false), fieldName, badRequestIfMissing, RequestFieldBinding.Form);

        /// <summary>Binds a <see cref="FormFieldAttribute"/> parameter of a converted type.</summary>
        /// <typeparam name="T">The parameter's type.</typeparam>
        /// <param name="controller">The controller.</param>
        /// <param name="fieldName">The field name.</param>
        /// <param name="badRequestIfMissing">The attribute's <see cref="FormFieldAttribute.BadRequestIfMissing"/>.</param>
        /// <param name="parameterName">The parameter's name.</param>
        /// <param name="converter">The type converter, or <see langword="null"/> for strings and enumerations.</param>
        /// <returns>A task whose result is the converted last value, or the default value if the field is absent.</returns>
        public static async Task<T?> FormFieldAsync<T>(WebApiController controller, string fieldName, bool badRequestIfMissing, string parameterName, TypeConverter? converter)
            => RequestFieldBinding.Get<T>(await FormDataAsync(controller).ConfigureAwait(false), fieldName, badRequestIfMissing, RequestFieldBinding.Form, parameterName, converter);

        /// <summary>Binds a <see cref="FormFieldAttribute"/> parameter of a converted array type.</summary>
        /// <typeparam name="TElement">The array's element type.</typeparam>
        /// <param name="controller">The controller.</param>
        /// <param name="fieldName">The field name.</param>
        /// <param name="badRequestIfMissing">The attribute's <see cref="FormFieldAttribute.BadRequestIfMissing"/>.</param>
        /// <param name="converter">The type converter, or <see langword="null"/> for strings and enumerations.</param>
        /// <returns>A task whose result is the converted values.</returns>
        public static async Task<TElement?[]> FormFieldArrayAsync<TElement>(WebApiController controller, string fieldName, bool badRequestIfMissing, TypeConverter? converter)
            => RequestFieldBinding.GetArray<TElement>(await FormDataAsync(controller).ConfigureAwait(false), fieldName, badRequestIfMissing, RequestFieldBinding.Form, converter);

        /// <summary>
        /// Binds a <see cref="JsonDataAttribute"/> parameter with EmbedIO's default JSON options,
        /// as the reflection-based registration does.
        /// </summary>
        /// <typeparam name="T">The parameter's type.</typeparam>
        /// <param name="controller">The controller.</param>
        /// <param name="parameterName">The parameter's name.</param>
        /// <returns>A task whose result is the deserialized request body.</returns>
        /// <exception cref="NotSupportedException">Reflection-based JSON is disabled, for example by
        /// trimming or Native AOT; set <see cref="GeneratedControllerBindingAttribute.JsonSerializerContext"/>.</exception>
        /// <exception cref="HttpException">The body cannot be deserialized (400 Bad Request).</exception>
        public static async Task<T?> JsonDataAsync<T>(WebApiController controller, string parameterName)
        {
            var body = await ReadBodyAsync(controller).ConfigureAwait(false);
            if (!ReflectionSerialization.IsSupported)
                throw ReflectionSerialization.Disabled();

            object? result;
            try
            {
                result = Json.Deserialize(body, typeof(T));
            }
            catch (JsonException)
            {
                throw HttpException.BadRequest($"Expected request body to be deserializable to {typeof(T).FullName}.");
            }

            return RequestFieldBinding.CastResult<T>(parameterName, result);
        }

        /// <summary>
        /// Binds a <see cref="JsonDataAttribute"/> parameter with the metadata of a
        /// <see cref="JsonSerializerContext"/>, without reflection.
        /// </summary>
        /// <typeparam name="T">The parameter's type.</typeparam>
        /// <param name="controller">The controller.</param>
        /// <param name="context">The JSON serializer context.</param>
        /// <returns>A task whose result is the deserialized request body.</returns>
        /// <exception cref="NotSupportedException"><paramref name="context"/> has no metadata for <typeparamref name="T"/>.</exception>
        /// <exception cref="HttpException">The body cannot be deserialized (400 Bad Request).</exception>
        public static async Task<T?> JsonDataAsync<T>(WebApiController controller, JsonSerializerContext context)
        {
            context = context ?? throw new ArgumentNullException(nameof(context));
            var body = await ReadBodyAsync(controller).ConfigureAwait(false);
            try
            {
                return Json.DeserializeWith<T>(body, context.Options);
            }
            catch (JsonException)
            {
                throw HttpException.BadRequest($"Expected request body to be deserializable to {typeof(T).FullName}.");
            }
        }

        /// <summary>
        /// Casts the result of an <see cref="IRequestDataAttribute{TController}"/> to the parameter's type,
        /// as the reflection-based registration does.
        /// </summary>
        /// <typeparam name="T">The parameter's type.</typeparam>
        /// <param name="parameterName">The parameter's name.</param>
        /// <param name="value">The value returned by the attribute.</param>
        /// <returns>The value, cast to <typeparamref name="T"/>.</returns>
        /// <exception cref="InvalidCastException">The value is not a <typeparamref name="T"/>.</exception>
        public static T? CastRequestData<T>(string parameterName, object? value)
            => RequestFieldBinding.CastResult<T>(parameterName, value);

        /// <summary>
        /// Serializes the result of a controller method that returns <see cref="Task{TResult}"/>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="serializer">The module's response serializer.</param>
        /// <param name="context">The HTTP context.</param>
        /// <param name="task">The task returned by the controller method.</param>
        /// <returns>A task representing the serialization.</returns>
        public static async Task SerializeResultAsync<TResult>(ResponseSerializerCallback serializer, IHttpContext context, Task<TResult> task)
        {
            serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            await serializer(context, await (task ?? throw new ArgumentNullException(nameof(task))).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private static System.Collections.Specialized.NameValueCollection QueryData(WebApiController controller)
            => (controller ?? throw new ArgumentNullException(nameof(controller))).HttpContext.GetRequestQueryData();

        private static Task<System.Collections.Specialized.NameValueCollection> FormDataAsync(WebApiController controller)
            => (controller ?? throw new ArgumentNullException(nameof(controller))).HttpContext.GetRequestFormDataAsync();

        private static async Task<string> ReadBodyAsync(WebApiController controller)
        {
            using var reader = (controller ?? throw new ArgumentNullException(nameof(controller))).HttpContext.OpenRequestText();
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
