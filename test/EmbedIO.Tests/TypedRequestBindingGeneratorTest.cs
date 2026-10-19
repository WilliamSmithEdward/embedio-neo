using System;
using System.IO;
using System.Linq;
using EmbedIO.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class TypedRequestBindingGeneratorTest
    {
        [TestCase("[QueryObject] M value", "public class M {public M(int value){} public int Value{get;set;}}")]
        [TestCase("[QueryObject] M value", "public class M {public int Value{get;init;}}")]
        [TestCase("[QueryObject] M value", "public class M {public required int Value{get;set;}}")]
        [TestCase("[QueryObject] M value", "public class M {public object Value{get;set;} = new object();}")]
        [TestCase("[QueryObject] M value", "public class M {public int Value{get;set;} public int value{get;set;}}")]
        [TestCase(
            "[QueryObject] M value",
            "public class A {public int Value{get;set;}} public class M:A {public new int Value{get;set;}}"
        )]
        [TestCase("[QueryObject, JsonData] M value", "public class M {public int Value{get;set;}}")]
        [TestCase("[FormObject] M value, [FormField] int count", "public class M {public int Value{get;set;}}")]
        [TestCase("[HeaderField(\"bad name\")] int value", "")]
        [TestCase("[CookieField(\"é\")] int value", "")]
        [TestCase("[RouteField] int[] value", "")]
        [TestCase("[HeaderField(DuplicateValues=(RequestDuplicateValues)99)] int value", "")]
        [TestCase("[QueryObject] M value", "public class M {[RequestField(\"\")] public int Value{get;set;}}")]
        public void UnsupportedBindingShapesProduceDiagnosticsInsteadOfGeneratorCrashes(string parameter, string model)
        {
            var result = Generate(parameter, model);
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(result.Diagnostics.Select(d => d.Id), Does.Contain("EMBEDIO009"));
            Assert.That(result.Diagnostics.All(d => d.Id != "CS8785"), Is.True);
        }

        [Test]
        public void ReferenceTryParseWithoutNullabilityAnnotationsCompilesSafely()
        {
            var result = Generate(
                "[HeaderField] M value",
                "public sealed class M {public static bool TryParse(string value, out M? result){result=null;return true;}}"
            );
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(
                result.Output
                    .GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error),
                Is.Empty
            );
        }

        [Test]
        public void NullableProviderTryParseSignaturesCompile()
        {
            var result = Generate(
                "[HeaderField] M value",
                "\n#nullable enable\npublic readonly struct M {public static bool TryParse(string? value, IFormatProvider? provider, out M result){result=default;return true;}}"
            );
            using var assertions = Assert.EnterMultipleScope();
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(
                result.Output
                    .GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error),
                Is.Empty
            );
        }

        private static (
            System.Collections.Immutable.ImmutableArray<Diagnostic> Diagnostics,
            Compilation Output
        ) Generate(string parameter, string model)
        {
            var source = "using System;using EmbedIO;using EmbedIO.WebApi;using EmbedIO.Routing;[GeneratedControllerBinding(typeof(C))]internal static partial class B{} public sealed class C:WebApiController{[Route(HttpVerbs.Get,\"/\")]public string Get("
                + parameter
                + ")=>\"ok\";}"
                + model;
            var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException("Missing test platform references.")).Split(Path.PathSeparator);
            var references = paths
                .Append(typeof(WebServer).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
            var parse = new CSharpParseOptions(LanguageVersion.Latest);
            var input = CSharpCompilation.Create(
                "TypedRequestBindingProbe",
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
