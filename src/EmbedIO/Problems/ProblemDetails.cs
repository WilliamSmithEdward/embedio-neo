using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbedIO.Utilities;

namespace EmbedIO.Problems
{
    /// <summary>
    /// <para>An RFC 9457 problem details object: a machine-readable description of an HTTP API error,
    /// sent as <c>application/problem+json</c>.</para>
    /// <para>Throw a <see cref="ProblemDetailsException"/> to send one, or let
    /// <see cref="ProblemDetailsHandler"/> describe other exceptions.</para>
    /// </summary>
    public sealed class ProblemDetails
    {
        // Members RFC 9457 defines; an extension cannot take their names.
        private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
        {
            "type", "title", "status", "detail", "instance"
        };
        private readonly Dictionary<string, JsonNode?> _extensions = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="ProblemDetails"/> class.
        /// </summary>
        /// <param name="status">The HTTP status code, 400 to 599.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not a client or server error status.</exception>
        public ProblemDetails(int status)
        {
            if (status < 400 || status > 599)
                throw new ArgumentOutOfRangeException(
                    nameof(status),
                    status,
                    "A problem has a client or server error status, 400 to 599."
                );

            Status = status;
        }

        /// <summary>Creates a problem using the standard status enumeration.</summary>
        /// <param name="status">An error status, 400 to 599.</param>
        public ProblemDetails(HttpStatusCode status) : this((int)status) { }

        /// <summary>Gets the HTTP status code.</summary>
        public int Status { get; }

        /// <summary>
        /// Gets or sets a URI reference identifying the problem type. When <see langword="null"/>, the
        /// default, the member is left out, which RFC 9457 reads as <c>about:blank</c>: the problem is
        /// no more than its status.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets a short summary of the problem type. When <see langword="null"/> and
        /// <see cref="Type"/> is not set, the status's reason phrase is sent.
        /// </summary>
        public string? Title { get; set; }

        /// <summary>Gets or sets an explanation specific to this occurrence of the problem.</summary>
        public string? Detail { get; set; }

        /// <summary>Gets or sets a URI reference identifying this occurrence of the problem.</summary>
        public string? Instance { get; set; }

        /// <summary>Gets the extension members, in the order they were added.</summary>
        public IReadOnlyDictionary<string, JsonNode?> Extensions => _extensions;

        /// <summary>
        /// Adds an extension member, such as a list of validation errors.
        /// </summary>
        /// <param name="name">The member's name. It cannot be one RFC 9457 defines.</param>
        /// <param name="value">The member's value, built with <see cref="JsonNode"/>, <see cref="JsonValue"/>,
        /// <see cref="JsonObject"/> or <see cref="JsonArray"/>, so serializing it needs no reflection.</param>
        /// <returns>This object.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty, reserved, or already added;
        /// or <paramref name="value"/> already belongs to another JSON document.</exception>
        public ProblemDetails WithExtension(string name, JsonNode? value)
        {
            name = Validate.NotNullOrEmpty(nameof(name), name);

            if (Reserved.Contains(name))
                throw new ArgumentException(
                    $"'{name}' is a member RFC 9457 defines; set its property instead.",
                    nameof(name)
                );

            if (_extensions.ContainsKey(name))
                throw new ArgumentException($"The extension '{name}' is already added.", nameof(name));

            if (value?.Parent != null)
                throw new ArgumentException("The value already belongs to another JSON document.", nameof(value));

            _extensions.Add(name, value);
            return this;
        }

        /// <summary>Writes the problem as JSON.</summary>
        /// <param name="indented">Whether to indent the output.</param>
        /// <returns>The JSON text.</returns>
        public string ToJson(bool indented = false)
        {
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
            {
                writer.WriteStartObject();

                if (Type != null)
                    writer.WriteString("type", Type);

                var title = Title ?? (Type == null ? ReasonPhrase(Status) : null);

                if (title != null)
                    writer.WriteString("title", title);

                writer.WriteNumber("status", Status);

                if (Detail != null)
                    writer.WriteString("detail", Detail);

                if (Instance != null)
                    writer.WriteString("instance", Instance);

                foreach (var extension in _extensions)
                {
                    writer.WritePropertyName(extension.Key);

                    if (extension.Value == null)
                        writer.WriteNullValue();

                    else
                        extension.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        // A copy with its own extension values, so handlers never change a problem that is thrown repeatedly.
        internal ProblemDetails Copy()
        {
            var copy = new ProblemDetails(Status)
            {
                Type = Type,
                Title = Title,
                Detail = Detail,
                Instance = Instance
            };

            foreach (var extension in _extensions)
                copy._extensions.Add(extension.Key, extension.Value?.DeepClone());

            return copy;
        }

        internal static string? ReasonPhrase(int status)
        {
            var phrase = EmbedIO.Net.Internal.HttpListenerResponseHelper.GetStatusDescription(status);
            return phrase.Length > 0 ? phrase : null;
        }
    }

    /// <summary>
    /// An HTTP exception that carries a <see cref="ProblemDetails"/> object, sent as is by
    /// <see cref="ProblemDetailsHandler"/>. Other HTTP exception handlers see an ordinary
    /// <see cref="HttpException"/> whose <see cref="HttpException.DataObject"/> is the problem.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="ProblemDetailsException"/> class.
    /// </remarks>
    /// <param name="problem">The problem to send.</param>
    /// <exception cref="ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
    public class ProblemDetailsException(ProblemDetails problem) : HttpException(
        Validate.NotNull(nameof(problem), problem).Status,
        problem.Detail ?? problem.Title,
        problem
    )
    {
        /// <summary>Gets the problem to send.</summary>
        public ProblemDetails Problem { get; } = problem;
    }
}
