using System;
using System.IO;
using System.Linq;
using EmbedIO.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class SecurityHeadersGeneratorTest
    {
        [TestCase("null")]
        [TestCase("\"\"")]
        [TestCase("\"bad name\"")]
        [TestCase("\"bad\\r\\nname\"")]
        [TestCase("\"é\"")]
        public void InvalidPolicyNamesProduceDedicatedDiagnostic(string literal)
        {
            var result = Generate(literal);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Diagnostics.Select(d => d.Id), Does.Contain("EMBEDIO017"));
                Assert.That(result.Diagnostics.All(d => d.Id != "CS8785"), Is.True);
            }
        }

        [Test]
        public void QuotedPolicyNamesAreEscapedInGeneratedCode()
        {
            var result = Generate("\"name\\\"quoted\"");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Diagnostics, Is.Empty);
                Assert.That(
                    result.Output
                        .GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error),
                    Is.Empty
                );
            }
        }

        private static (
            System.Collections.Immutable.ImmutableArray<Diagnostic> Diagnostics,
            Compilation Output
        ) Generate(string literal)
        {
            var source = "using EmbedIO;using EmbedIO.WebApi;using EmbedIO.Routing;using EmbedIO.Security;[GeneratedControllerBinding(typeof(C))]internal static partial class B{}[SecurityHeaders("
                + literal
                + ")]public sealed class C:WebApiController{[Route(HttpVerbs.Get,\"/\")]public string Get()=>\"ok\";}";
            var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException("Missing test platform references.")).Split(Path.PathSeparator);
            var references = paths
                .Append(typeof(WebServer).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
            var parse = new CSharpParseOptions(LanguageVersion.Latest);
            var input = CSharpCompilation.Create(
                "SecurityHeadersGeneratorProbe",

                [
                    // One application compilation unit.
                    CSharpSyntaxTree.ParseText(source, parse)
                ],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );
            var driver = CSharpGeneratorDriver.Create(

                [
                    // Exercise the shipped incremental generator.
                    new ControllerBindingGenerator().AsSourceGenerator()
                ],
                parseOptions: parse
            );
            driver.RunGeneratorsAndUpdateCompilation(input, out var output, out var diagnostics);
            return (diagnostics, output);
        }
    }
}
