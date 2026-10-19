using System;
using System.Collections.Generic;
using System.Net;
using EmbedIO.OpenApi;

namespace EmbedIO.Endpoints
{
    /// <summary>Configures one exact endpoint before its module is compiled.</summary>
    public sealed class EndpointBuilder : EndpointOptions<EndpointBuilder>
    {
        internal EndpointBuilder(
            EndpointModule owner,
            EndpointRouteGroup parent,
            string route,
            HttpVerbs[] methods,
            EndpointHandler handler
        ) : base(owner, parent)
        {
            Route = route;
            Methods = Array.AsReadOnly((HttpVerbs[])methods.Clone());
            Handler = handler;
        }

        internal readonly EndpointHandler Handler;
        internal string? Name;
        internal bool Excluded;
        internal string? Summary, Description;
        internal readonly List<OpenApiResponseAttribute> Responses = [];

        /// <summary>Gets the complete route template.</summary>
        public string Route { get; }

        /// <summary>Gets the selected HTTP methods.</summary>
        public IReadOnlyList<HttpVerbs> Methods { get; }

        /// <summary>Assigns a unique application-relative link name and OpenAPI operation ID.</summary>
        public EndpointBuilder WithName(string name)
        {
            Check();

            if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
                throw new ArgumentException("Use a nonempty name of at most 128 characters.", nameof(name));

            Name = name;
            return this;
        }

        /// <summary>Overrides the OpenAPI summary for this endpoint.</summary>
        public EndpointBuilder WithSummary(string summary)
        {
            Check();
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            return this;
        }

        /// <summary>Overrides the OpenAPI description for this endpoint.</summary>
        public EndpointBuilder WithDescription(string description)
        {
            Check();
            Description = description ?? throw new ArgumentNullException(nameof(description));
            return this;
        }

        /// <summary>Declares an OpenAPI response body; this does not change the executed HTTP result.</summary>
        public EndpointBuilder Produces<T>(int statusCode, string? contentType = null) =>
            AddResponse(statusCode, typeof(T), contentType);

        /// <summary>Declares an OpenAPI response body using a standard status.</summary>
        public EndpointBuilder Produces<T>(HttpStatusCode statusCode, string? contentType = null) =>
            Produces<T>((int)statusCode, contentType);

        /// <summary>Declares an OpenAPI response without a body.</summary>
        public EndpointBuilder Produces(int statusCode) => AddResponse(statusCode, null, null);

        /// <summary>Declares an OpenAPI response without a body using a standard status.</summary>
        public EndpointBuilder Produces(HttpStatusCode statusCode) => Produces((int)statusCode);

        private EndpointBuilder AddResponse(int statusCode, Type? type, string? contentType)
        {
            Check();

            if (statusCode < 100 || statusCode > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));

            if (Responses.Exists(r => r.StatusCode == statusCode))
                throw new ArgumentException("Response status already declared.", nameof(statusCode));

            Responses.Add(new OpenApiResponseAttribute(statusCode)
            {
                Type = type,
                ContentType = contentType
            });
            return this;
        }

        /// <summary>Explicitly excludes this endpoint from generated OpenAPI documents.</summary>
        public EndpointBuilder ExcludeFromDescription()
        {
            Check();
            Excluded = true;
            return this;
        }
    }
}
