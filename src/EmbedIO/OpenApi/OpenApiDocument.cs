using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EmbedIO.Utilities;

namespace EmbedIO.OpenApi
{
    /// <summary>An OpenAPI 3.1 document built by <see cref="OpenApiDocumentBuilder"/>.</summary>
    public sealed class OpenApiDocument
    {
        internal OpenApiDocument(JsonObject root, IReadOnlyList<string> problems)
        {
            Root = root;
            Problems = problems;
        }

        /// <summary>Gets the document's JSON. Changes to it are reflected in <see cref="ToJson"/>.</summary>
        public JsonObject Root { get; }

        /// <summary>
        /// Gets what the builder could not describe and left out. Empty unless the document was
        /// built with <c>allowIncomplete</c>.
        /// </summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>Writes the document as UTF-8 JSON text.</summary>
        /// <param name="indented"><see langword="true"/> to indent the output.</param>
        /// <returns>The JSON text.</returns>
        public string ToJson(bool indented = false)
        {
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
                Root.WriteTo(writer);

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    /// <summary>The exception thrown when an OpenAPI document cannot describe every route.</summary>
    public sealed class OpenApiDocumentException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="OpenApiDocumentException"/> class.</summary>
        public OpenApiDocumentException() : this([]) { }

        /// <summary>Initializes a new instance of the <see cref="OpenApiDocumentException"/> class.</summary>
        /// <param name="message">The message.</param>
        public OpenApiDocumentException(string message) : base(message) => Problems = [message];

        /// <summary>Initializes a new instance of the <see cref="OpenApiDocumentException"/> class.</summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The exception that caused this one.</param>
        public OpenApiDocumentException(string message, Exception innerException) : base(message, innerException) =>
            Problems = [message];

        /// <summary>Initializes a new instance of the <see cref="OpenApiDocumentException"/> class.</summary>
        /// <param name="problems">What the document could not describe.</param>
        public OpenApiDocumentException(IReadOnlyList<string> problems) : base(BuildMessage(problems)) =>
            Problems = problems ?? [];

        /// <summary>Gets what the document could not describe.</summary>
        public IReadOnlyList<string> Problems { get; }

        private static string BuildMessage(IReadOnlyList<string>? problems) =>
            problems == null || problems.Count == 0
                ? "The OpenAPI document cannot describe every route."
                : "The OpenAPI document cannot describe every route:"
                    + string.Concat(problems.Select(p => Environment.NewLine + "- " + p));
    }

    /// <summary>
    /// Serves an OpenAPI document at one path, for <c>GET</c> and <c>HEAD</c> requests, and passes
    /// every other request to the next module.
    /// </summary>
    public sealed class OpenApiDocumentModule : WebModuleBase
    {
        /// <summary>Initializes a new instance of the <see cref="OpenApiDocumentModule"/> class.</summary>
        /// <param name="path">The document's URL path, such as <c>/openapi.json</c>.</param>
        /// <param name="document">The document. Its JSON is written once, here.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="document"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is not a valid URL path.</exception>
        public OpenApiDocumentModule(string path, OpenApiDocument document) : base("/")
        {
            _path = Validate.Route(nameof(path), path, false);
            _json = Encoding.UTF8.GetBytes(
                Validate
                    .NotNull(nameof(document), document)
                    .ToJson()
            );
        }

        private readonly string _path;
        private readonly byte[] _json;

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <inheritdoc />
        protected override async Task OnRequestAsync(IHttpContext context)
        {
            context = Validate.NotNull(nameof(context), context);

            if (
                !string.Equals(context.RequestedPath, _path, StringComparison.Ordinal)
                || (context.Request.HttpVerb != HttpVerbs.Get && context.Request.HttpVerb != HttpVerbs.Head)
            )
                return;

            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = _json.Length;

            if (context.Request.HttpVerb == HttpVerbs.Get)
                await context.Response.OutputStream
                    .WriteAsync(_json, 0, _json.Length, context.CancellationToken)
                    .ConfigureAwait(false);

            context.SetHandled();
        }
    }
}
