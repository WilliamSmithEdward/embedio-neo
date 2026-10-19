using System;
using System.Net;

namespace EmbedIO.OpenApi
{
    /// <summary>
    /// Describes a Web API controller method in OpenAPI documents. Every property is optional;
    /// without this attribute the operation is tagged with the controller name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public sealed class OpenApiOperationAttribute : Attribute
    {
        /// <summary>Gets or sets a short summary of what the operation does.</summary>
        public string? Summary { get; set; }

        /// <summary>Gets or sets a longer description; CommonMark is allowed.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// Gets or sets the operation's unique ID. The default is the controller name,
        /// without a <c>Controller</c> suffix, an underscore and the method name.
        /// </summary>
        public string? OperationId { get; set; }

        /// <summary>Gets or sets the operation's tags. The default is the controller name, without a <c>Controller</c> suffix.</summary>
        public string[]? Tags { get; set; }

        /// <summary>Gets or sets a value indicating whether the operation is deprecated.</summary>
        public bool Deprecated { get; set; }
    }

    /// <summary>
    /// Declares a response of a Web API controller method in OpenAPI documents. Declaring a
    /// 2xx response replaces the success response described from the method's return type.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="OpenApiResponseAttribute"/> class.
    /// </remarks>
    /// <param name="statusCode">The HTTP status code, from 100 to 599.</param>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class OpenApiResponseAttribute(int statusCode) : Attribute
    {

        /// <summary>Declares a response using the standard status enumeration.</summary>
        /// <param name="statusCode">The status code.</param>
        public OpenApiResponseAttribute(HttpStatusCode statusCode) : this((int)statusCode) { }

        /// <summary>Gets the HTTP status code.</summary>
        public int StatusCode { get; } = statusCode;

        /// <summary>Gets or sets the response description. The default is the status code's reason phrase.</summary>
        public string? Description { get; set; }

        /// <summary>Gets or sets the type of the response body; <see langword="null"/> for no body or an unstated one.
        /// A JSON media type takes its schema from the serializer options; other media types accept
        /// <see cref="string"/>, a byte array or a <see cref="System.IO.Stream"/>.</summary>
        public Type? Type { get; set; }

        /// <summary>Gets or sets the body's media type. The default is <c>application/json</c>.</summary>
        public string? ContentType { get; set; }
    }

    /// <summary>Excludes a Web API controller, or one of its methods, from OpenAPI documents.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public sealed class OpenApiIgnoreAttribute : Attribute { }
}
