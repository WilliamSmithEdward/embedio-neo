using System;
using System.IO;
using System.Linq;
using EmbedIO.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class MinimalEndpointsGeneratorTest
    {
        [TestCase("internal static class E { public static string Get()=>string.Empty; }")]
        [TestCase("internal partial class E { public string Get()=>string.Empty; }")]
        [TestCase("internal static partial class E<T> { public static string Get()=>string.Empty; }")]
        [TestCase("internal static partial class E { public static string Get<T>()=>string.Empty; }")]
        [TestCase(
            "internal static partial class E { public static string Get()=>string.Empty; public static string Get(int id)=>string.Empty; }"
        )]
        [TestCase(
            "internal static partial class E { public static string Get()=>string.Empty; public static int GetEndpoint=>0; }"
        )]
        [TestCase("internal static partial class E { private static string Get()=>string.Empty; }")]
        [TestCase(
            "internal static partial class E { public static async void Get(){await System.Threading.Tasks.Task.Yield();} }"
        )]
        [TestCase(
            "internal static partial class E { public static string Get([JsonData] string a,[JsonData] string b)=>string.Empty; }"
        )]
        [TestCase(
            "internal static partial class E { public static string Get([JsonData] string a,[FormField] string b)=>string.Empty; }"
        )]
        [TestCase("[BeforeHandler] internal static partial class E { public static string Get()=>string.Empty; }")]
        [TestCase("internal static partial class E { [BeforeHandler] public static string Get()=>string.Empty; }")]
        public void InvalidDeclarationsHaveActionableDiagnostics(string declaration)
        {
            var result = Generate(declaration, LanguageVersion.Latest);
            Assert.That(result.Diagnostics.Select(d => d.Id), Does.Contain("EMBEDIO018"));
            Assert.That(result.Diagnostics.Select(d => d.Id), Does.Not.Contain("CS8785"));
        }

        [TestCase(LanguageVersion.CSharp7_3)]
        [TestCase(LanguageVersion.Latest)]
        public void GeneratedFrameworkAndValueTaskBindingCompilesWithoutDynamicCode(LanguageVersion language)
        {
            var result = Generate(
                "internal static partial class E { public static System.Threading.Tasks.ValueTask<string> Get(EmbedIO.IHttpContext context, System.Threading.CancellationToken cancellationToken)=>new System.Threading.Tasks.ValueTask<string>(context.Request.HttpMethod); }",
                language
            );

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

        [TestCase("ref int value")]
        [TestCase("[QueryObject] object value")]
        [TestCase("[RouteField] int[] value")]
        [TestCase("[HeaderField(\"bad name\")] int value")]
        public void UnsupportedBindingsFailAtBuildTime(string parameter)
        {
            var result = Generate(
                "internal static partial class E { public static string Get(" + parameter + ")=>string.Empty; }",
                LanguageVersion.Latest
            );
            Assert.That(result.Diagnostics.Select(d => d.Id), Does.Contain("EMBEDIO009"));
        }

        private static (
            System.Collections.Immutable.ImmutableArray<Diagnostic> Diagnostics,
            Compilation Output
        ) Generate(string declaration, LanguageVersion language)
        {
            var source = "using System;using EmbedIO;using EmbedIO.WebApi;using EmbedIO.Endpoints;[GeneratedEndpointBinding]"
                + declaration;
            var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException("Missing references.")).Split(Path.PathSeparator);
            var references = paths
                .Append(typeof(WebServer).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
            var parse = new CSharpParseOptions(language);
            var input = CSharpCompilation.Create(
                "MinimalEndpointProbe",
                [CSharpSyntaxTree.ParseText(source, parse)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );
            var driver = CSharpGeneratorDriver.Create(
                [new ControllerBindingGenerator().AsSourceGenerator()],
                parseOptions: parse
            );
            driver.RunGeneratorsAndUpdateCompilation(input, out var output, out var diagnostics);
            return (diagnostics, output);
        }
    }
}
