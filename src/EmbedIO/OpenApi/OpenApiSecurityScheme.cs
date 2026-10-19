using System;
using System.Text.Json.Nodes;
using EmbedIO.Utilities;

namespace EmbedIO.OpenApi
{
    /// <summary>
    /// Where a parameter or an API key is sent.
    /// </summary>
    public enum OpenApiParameterLocation
    {
        /// <summary>The query string.</summary>
        Query,

        /// <summary>A request header.</summary>
        Header,

        /// <summary>A parameter of the route.</summary>
        Path,

        /// <summary>A cookie.</summary>
        Cookie
    }

    /// <summary>
    /// An OpenAPI security scheme: how a client authenticates. Add it with
    /// <see cref="OpenApiDocumentBuilder.AddSecurityScheme"/> and apply it with
    /// <see cref="OpenApiDocumentBuilder.RequireSecurity(IWebModule, string)"/>.
    /// </summary>
    public sealed class OpenApiSecurityScheme
    {
        private readonly JsonObject _definition;

        private OpenApiSecurityScheme(JsonObject definition, string? description)
        {
            if (description != null)
                definition["description"] = description;

            _definition = definition;
        }

        /// <summary>HTTP Basic authentication, as <see cref="Authentication.BasicAuthenticationModule"/> performs it.</summary>
        /// <param name="description">An optional description; CommonMark is allowed.</param>
        /// <returns>The scheme.</returns>
        public static OpenApiSecurityScheme Basic(string? description = null) =>
            new(
                new JsonObject
                {
                    ["type"] = "http",
                    ["scheme"] = "basic"
                },
                description
            );

        /// <summary>HTTP Bearer authentication: a token in the <c>Authorization</c> header.</summary>
        /// <param name="bearerFormat">An optional hint at the token's format, such as <c>JWT</c>.</param>
        /// <param name="description">An optional description; CommonMark is allowed.</param>
        /// <returns>The scheme.</returns>
        public static OpenApiSecurityScheme Bearer(string? bearerFormat = null, string? description = null)
        {
            var definition = new JsonObject
            {
                ["type"] = "http",
                ["scheme"] = "bearer"
            };

            if (bearerFormat != null)
                definition["bearerFormat"] = bearerFormat;

            return new(definition, description);
        }

        /// <summary>An API key sent in a header, the query string or a cookie.</summary>
        /// <param name="name">The name of the header, query field or cookie.</param>
        /// <param name="location">Where the key is sent.</param>
        /// <param name="description">An optional description; CommonMark is allowed.</param>
        /// <returns>The scheme.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or
        /// <paramref name="location"/> is <see cref="OpenApiParameterLocation.Path"/>, which OpenAPI does not allow for a key.</exception>
        public static OpenApiSecurityScheme ApiKey(
            string name,
            OpenApiParameterLocation location,
            string? description = null
        )
        {
            name = Validate.NotNullOrEmpty(nameof(name), name);
            var where = location switch
            {
                OpenApiParameterLocation.Header => "header",
                OpenApiParameterLocation.Query => "query",
                OpenApiParameterLocation.Cookie => "cookie",
                _ =>
                    throw new ArgumentException(
                        "An API key is sent in a header, the query string or a cookie.",
                        nameof(location)
                    )
            };

            return new(
                new JsonObject
                {
                    ["type"] = "apiKey",
                    ["name"] = name,
                    ["in"] = where
                },
                description
            );
        }

        internal JsonObject ToJson() => (JsonObject)_definition.DeepClone();
    }
}
