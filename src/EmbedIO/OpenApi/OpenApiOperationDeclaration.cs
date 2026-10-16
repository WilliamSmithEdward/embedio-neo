using System;
using System.Net;
using System.Collections.Generic;
using EmbedIO.Utilities;

namespace EmbedIO.OpenApi
{
    /// <summary>
    /// <para>Describes an operation the document cannot read from a generated binding, such as a
    /// route handled by a <see cref="Routing.RoutingModule"/> or a custom module. Pass a callback
    /// that fills one in to <see cref="OpenApiDocumentBuilder.AddOperation"/>.</para>
    /// <para>The declaration states what the handler does; nothing checks it against the handler,
    /// so keep the two in step.</para>
    /// </summary>
    public sealed class OpenApiOperationDeclaration
    {
        internal OpenApiOperationDeclaration()
        {
        }

        /// <summary>Gets or sets a short summary of the operation.</summary>
        public string? Summary { get; set; }

        /// <summary>Gets or sets a longer description; CommonMark is allowed.</summary>
        public string? Description { get; set; }

        /// <summary>Gets or sets the operation ID. Without one the operation has none.</summary>
        public string? OperationId { get; set; }

        /// <summary>Gets or sets the operation's tags.</summary>
        public IReadOnlyList<string>? Tags { get; set; }

        /// <summary>Gets or sets a value indicating whether the operation is deprecated.</summary>
        public bool Deprecated { get; set; }

        internal List<DeclaredParameter> Parameters { get; } = new();

        internal DeclaredBody? Body { get; private set; }

        internal List<DeclaredResponse> Responses { get; } = new();

        /// <summary>
        /// Declares a parameter. Route parameters need no declaration unless they are not strings,
        /// since a route handler receives them as strings; they are always required.
        /// </summary>
        /// <param name="name">The parameter's name.</param>
        /// <param name="location">Where the parameter is sent.</param>
        /// <param name="type">The value's type, such as <see cref="int"/>, an enum or an array of either.</param>
        /// <param name="required">Whether the request must carry the parameter.</param>
        /// <param name="description">An optional description.</param>
        /// <returns>This declaration.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="type"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        public OpenApiOperationDeclaration Parameter(string name, OpenApiParameterLocation location, Type type, bool required = false, string? description = null)
        {
            Parameters.Add(new DeclaredParameter(
                Validate.NotNullOrEmpty(nameof(name), name),
                location,
                Validate.NotNull(nameof(type), type),
                required,
                description));
            return this;
        }

        /// <summary>
        /// Declares the request body. A JSON media type, such as the default, takes its schema
        /// from the serializer options passed to <see cref="OpenApiDocumentBuilder.Build(System.Text.Json.JsonSerializerOptions)"/>.
        /// </summary>
        /// <param name="type">The body's type, or <see langword="null"/> to leave its schema unstated.
        /// Other media types accept <see cref="string"/>, a byte array or a <see cref="System.IO.Stream"/>.</param>
        /// <param name="contentType">The body's media type.</param>
        /// <param name="required">Whether the request must carry a body.</param>
        /// <returns>This declaration.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="contentType"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="contentType"/> is empty.</exception>
        /// <exception cref="InvalidOperationException">A body is already declared.</exception>
        public OpenApiOperationDeclaration RequestBody(Type? type, string contentType = "application/json", bool required = true)
        {
            contentType = Validate.NotNullOrEmpty(nameof(contentType), contentType);
            if (Body != null)
                throw new InvalidOperationException("The operation's request body is already declared.");

            Body = new DeclaredBody(type, contentType, required);
            return this;
        }

        /// <summary>Declares a response. Declare every status the handler sends.</summary>
        /// <param name="statusCode">The HTTP status code.</param>
        /// <param name="description">The description; by default, the status's reason phrase.</param>
        /// <param name="type">The body's type, or <see langword="null"/> for a response without content.</param>
        /// <param name="contentType">The body's media type; by default <c>application/json</c> when
        /// <paramref name="type"/> is given.</param>
        /// <returns>This declaration.</returns>
        public OpenApiOperationDeclaration Response(int statusCode, string? description = null, Type? type = null, string? contentType = null)
        {
            Responses.Add(new DeclaredResponse(statusCode, description, type, contentType));
            return this;
        }

        /// <summary>Declares a response using the existing integer response metadata path.</summary>
        /// <param name="statusCode">The status code.</param>
        /// <param name="description">The optional description.</param>
        /// <param name="type">The optional body type.</param>
        /// <param name="contentType">The optional body media type.</param>
        /// <returns>This declaration.</returns>
        public OpenApiOperationDeclaration Response(HttpStatusCode statusCode, string? description = null, Type? type = null, string? contentType = null)
            => Response((int)statusCode, description, type, contentType);

        internal sealed class DeclaredParameter
        {
            internal DeclaredParameter(string name, OpenApiParameterLocation location, Type type, bool required, string? description)
            {
                Name = name;
                Location = location;
                Type = type;
                Required = required;
                Description = description;
            }

            internal string Name { get; }

            internal OpenApiParameterLocation Location { get; }

            internal Type Type { get; }

            internal bool Required { get; }

            internal string? Description { get; }
        }

        internal sealed class DeclaredBody
        {
            internal DeclaredBody(Type? type, string contentType, bool required)
            {
                Type = type;
                ContentType = contentType;
                Required = required;
            }

            internal Type? Type { get; }

            internal string ContentType { get; }

            internal bool Required { get; }
        }

        internal sealed class DeclaredResponse
        {
            internal DeclaredResponse(int statusCode, string? description, Type? type, string? contentType)
            {
                StatusCode = statusCode;
                Description = description;
                Type = type;
                ContentType = contentType;
            }

            internal int StatusCode { get; }

            internal string? Description { get; }

            internal Type? Type { get; }

            internal string? ContentType { get; }
        }
    }
}
