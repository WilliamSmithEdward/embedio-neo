using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using EmbedIO.Problems;

namespace EmbedIO
{
    internal sealed class ValidationProblemData
    {
        internal const int MaxErrors = 1024;
        internal const int MaxPathLength = 1024;
        internal const int MaxMessageLength = 4096;
        private const int MaxTextBytes = 65536;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
        private int _messages;
        private int _bytes;

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> Errors =>
            new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                _errors.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.AsReadOnly(),
                    StringComparer.Ordinal
                )
            );

        internal static ValidationProblemData FromDictionary(IReadOnlyDictionary<string, string[]> errors)
        {
            errors = Utilities.Validate.NotNull(nameof(errors), errors);

            if (errors.Count > MaxErrors) throw new ArgumentException("Too many validation fields.", nameof(errors));

            var data = new ValidationProblemData();
            var fields = 0;

            foreach (var pair in errors)
            {
                if (
                    ++fields > MaxErrors
                    || pair.Value == null
                    || pair.Value.Length == 0
                    || pair.Value.Length > MaxErrors
                )
                    throw new ArgumentException("Use bounded, nonempty message arrays.", nameof(errors));

                foreach (var message in pair.Value) data.Add(pair.Key, message);
            }

            data.RequireErrors();
            return data;
        }

        internal static ValidationProblemData FromAnnotations(IEnumerable<ValidationResult?> validationResults)
        {
            validationResults = Utilities.Validate.NotNull(nameof(validationResults), validationResults);
            var data = new ValidationProblemData();
            var count = 0;

            foreach (var result in validationResults)
            {
                if (++count > MaxErrors)
                    throw new ArgumentException("Too many validation results.", nameof(validationResults));

                if (result == null) continue;

                foreach (var member in result.MemberNames.DefaultIfEmpty(string.Empty))
                    data.Add(member, result.ErrorMessage ?? "The value is invalid.");
            }

            data.RequireErrors();
            return data;
        }

        private void Add(string path, string message)
        {
            if (
                path == null
                || path.Length > MaxPathLength
                || string.IsNullOrWhiteSpace(message)
                || message.Length > MaxMessageLength
            )
                throw new ArgumentException("Use bounded field paths and nonempty messages.");

            if (_messages >= MaxErrors) throw new ArgumentException("Too many validation messages.");

            var isNew = !_errors.TryGetValue(path, out var messages);
            // Strict encoding rejects ambiguous field names containing unmatched UTF-16 surrogates.
            var added = StrictUtf8.GetByteCount(message) + (isNew ? StrictUtf8.GetByteCount(path) : 0);

            if (added > MaxTextBytes - _bytes) throw new ArgumentException("Validation text exceeds 64 KiB UTF-8.");

            if (messages == null)
            {
                messages = [];
                _errors.Add(path, messages);
            }

            messages.Add(message);
            _messages++;
            _bytes += added;
        }

        private void RequireErrors()
        {
            if (_messages == 0) throw new ArgumentException("At least one validation error is required.");
        }

        internal ProblemDetails CreateProblem(string? detail)
        {
            var fields = new JsonObject();

            foreach (var pair in _errors)
            {
                var messages = new JsonArray();

                foreach (var message in pair.Value) messages.Add((JsonNode?)JsonValue.Create(message));

                fields.Add(pair.Key, messages);
            }

            return new ProblemDetails(400)
            {
                Title = "Request validation failed.",
                Detail = detail
            }.WithExtension("errors", fields);
        }
    }
}
