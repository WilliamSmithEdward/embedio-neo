using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace EmbedIO.Generators
{
    // The parser's output: generated source plus diagnostics, as values that compare by
    // content, so the incremental pipeline can skip emission when nothing changed. No
    // symbols or syntax nodes are kept, so compilations are not retained between runs.
    internal sealed class BindingModel : IEquatable<BindingModel>
    {
        internal BindingModel(
            string hintName,
            string? source,
            ImmutableArray<DiagnosticInfo> diagnostics,
            ImmutableArray<DiagnosticInfo> trimDiagnostics
        )
        {
            HintName = hintName;
            Source = source;
            Diagnostics = diagnostics;
            TrimDiagnostics = trimDiagnostics;
        }

        internal string HintName { get; }

        // Null when an error prevents generation.
        internal string? Source { get; }
        internal ImmutableArray<DiagnosticInfo> Diagnostics { get; }

        // Reported only when the project runs the trim or Native AOT analyzer.
        internal ImmutableArray<DiagnosticInfo> TrimDiagnostics { get; }

        public bool Equals(BindingModel? other) =>
            other is not null
                && string.Equals(HintName, other.HintName, StringComparison.Ordinal)
                && string.Equals(Source, other.Source, StringComparison.Ordinal)
                && Diagnostics.SequenceEqual(other.Diagnostics)
                && TrimDiagnostics.SequenceEqual(other.TrimDiagnostics);

        public override bool Equals(object? obj) => Equals(obj as BindingModel);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(HintName);
    }

    internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
    {
        private DiagnosticInfo(
            DiagnosticDescriptor descriptor,
            string? filePath,
            TextSpan span,
            LinePositionSpan lineSpan,
            ImmutableArray<string> arguments
        )
        {
            Descriptor = descriptor;
            FilePath = filePath;
            Span = span;
            LineSpan = lineSpan;
            Arguments = arguments;
        }

        internal DiagnosticDescriptor Descriptor { get; }
        internal string? FilePath { get; }
        internal TextSpan Span { get; }
        internal LinePositionSpan LineSpan { get; }
        internal ImmutableArray<string> Arguments { get; }

        internal static DiagnosticInfo Create(
            DiagnosticDescriptor descriptor,
            Location? location,
            params string[] arguments
        )
        {
            var source = location is not null && location.IsInSource ? location : null;
            return new DiagnosticInfo(
                descriptor,
                source?.SourceTree?.FilePath,
                source?.SourceSpan ?? default,
                source?.GetLineSpan().Span ?? default,
                arguments.ToImmutableArray()
            );
        }

        internal Diagnostic ToDiagnostic()
        {
            var location = FilePath is null ? Location.None : Location.Create(FilePath, Span, LineSpan);
            return Diagnostic.Create(
                Descriptor,
                location,
                Arguments
                    .Cast<object>()
                    .ToArray()
            );
        }

        public bool Equals(DiagnosticInfo? other) =>
            other is not null
                && ReferenceEquals(Descriptor, other.Descriptor)
                && string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
                && Span.Equals(other.Span)
                && LineSpan.Equals(other.LineSpan)
                && Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

        public override int GetHashCode() => Span.GetHashCode();
    }
}
