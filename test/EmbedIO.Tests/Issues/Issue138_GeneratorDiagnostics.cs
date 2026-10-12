using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EmbedIO.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Build-time behavior of the controller-binding generator: the diagnostics it reports for
    // signatures it cannot bind, and the code it generates for those it can.
    [TestFixture]
    public sealed class Issue138_GeneratorDiagnostics
    {
        private const string Usings = "using System; using System.Threading.Tasks; using EmbedIO; using EmbedIO.Routing; using EmbedIO.WebApi;\n";

        // The test host's assemblies (which include EmbedIO) and this assembly, for Issue138Point.
        private static readonly Lazy<MetadataReference[]> References = new(() =>
            ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? throw new InvalidOperationException("No platform assemblies."))
                .Split(Path.PathSeparator)
                .Append(typeof(Issue138Point).Assembly.Location)
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToArray());

        [TestCase(LanguageVersion.CSharp7_3)]
        [TestCase(LanguageVersion.Latest)]
        public void ValidControllersGenerateCodeThatCompilesWithoutWarnings(LanguageVersion language)
        {
            var result = Run(Usings + @"
namespace App
{
    [GeneratedControllerBinding(typeof(PeopleController))]
    internal static partial class Bindings { }

    public sealed class PeopleController : WebApiController
    {
        [Route(HttpVerbs.Get, ""/people/{id}"")]
        public string Get(int id, [QueryField] string name, [QueryField] int[] tags, DayOfWeek day = DayOfWeek.Monday) { return name; }

        [Route(HttpVerbs.Post, ""/people"")]
        public async Task<int> Post([FormField] decimal amount, [JsonData] Person person) { await Task.Yield(); return 1; }

        [Route(HttpVerbs.Delete, ""/people/{id}"")]
        public void Delete(Guid id) { }
    }

    public sealed class Person { public string Name = string.Empty; }
}", language);

            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.CompilationDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning), Is.Empty, string.Join("\n", result.CompilationDiagnostics));
            Assert.That(result.Generated, Does.Contain("public static global::EmbedIO.WebApi.WebApiControllerBinding<global::App.PeopleController> PeopleController"));
        }

        [Test]
        public void NestedAndGlobalNamespaceContextsCompile()
        {
            var result = Run(Usings + @"
public sealed class C : WebApiController { [Route(HttpVerbs.Get, ""/"")] public string Get() { return """"; } }
[GeneratedControllerBinding(typeof(C))] internal static partial class GlobalBindings { }
namespace N { internal partial class Outer { internal partial struct Middle { [GeneratedControllerBinding(typeof(C))] internal partial class Inner { } } } }
internal static class Use { internal static object[] All() { return new object[] { GlobalBindings.C, N.Outer.Middle.Inner.C }; } }");

            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.CompilationDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning), Is.Empty, string.Join("\n", result.CompilationDiagnostics));
        }

        [Test]
        public void InternalControllersGetInternalBindings()
        {
            var result = Run(Usings + @"
internal sealed class Hidden : WebApiController { [Route(HttpVerbs.Get, ""/"")] public string Get() { return """"; } }
[GeneratedControllerBinding(typeof(Hidden))] public static partial class Bindings { }");

            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.Generated, Does.Contain("internal static global::EmbedIO.WebApi.WebApiControllerBinding<global::Hidden> Hidden"));
            Assert.That(result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        }

        private static IEnumerable<TestCaseData> Diagnostics()
        {
            const string Controller = "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get() { return \"\"; } }\n";
            TestCaseData Case(string id, string source, string name) => new TestCaseData(id, Usings + source).SetName(id + " " + name);

            yield return Case("EMBEDIO001", Controller + "[GeneratedControllerBinding(typeof(C))] internal static class B { }", "class not partial");
            yield return Case("EMBEDIO001", Controller + "[GeneratedControllerBinding(typeof(C))] internal partial class B<T> { }", "generic class");
            yield return Case("EMBEDIO001", Controller + "internal class O { [GeneratedControllerBinding(typeof(C))] internal partial class B { } }", "containing type not partial");
            yield return Case("EMBEDIO002", "[GeneratedControllerBinding(typeof(string))] internal static partial class B { }", "not a controller");
            yield return Case("EMBEDIO002", "public sealed class G<T> : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get() { return \"\"; } }\n[GeneratedControllerBinding(typeof(G<int>))] internal static partial class B { }", "generic controller");
            yield return Case("EMBEDIO002", "internal class O { private sealed class P : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get() { return \"\"; } } internal static System.Type T = typeof(P); }\n[GeneratedControllerBinding(typeof(O))] internal static partial class B { }", "not derived");
            yield return Case("EMBEDIO003", Controller + "namespace X { public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get() { return \"\"; } } }\n[GeneratedControllerBinding(typeof(C))][GeneratedControllerBinding(typeof(X.C))] internal static partial class B { }", "same name twice");
            yield return Case("EMBEDIO003", Controller + "[GeneratedControllerBinding(typeof(C))] internal static partial class B { internal static int C; }", "existing member");
            yield return Case("EMBEDIO004", Controller + "[GeneratedControllerBinding(typeof(C), JsonSerializerContext = typeof(string))] internal static partial class B { }", "not a context");
            yield return Case("EMBEDIO005", "public sealed class C : WebApiController { public string Get() { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "no routes");
            yield return Case("EMBEDIO006", "public sealed class GetAttribute : RouteAttribute { public GetAttribute(string r) : base(HttpVerbs.Get, r) { } }\npublic sealed class C : WebApiController { [Get(\"/\")] public string Get() { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "custom route attribute");
            yield return Case("EMBEDIO007", "public sealed class C : WebApiController { [Route(HttpVerbs.Get, null)] public string Get() { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "null route");
            yield return Case("EMBEDIO009", "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get(ref int x) { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "ref parameter");
            yield return Case("EMBEDIO009", "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get([QueryField] object x) { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "ambiguous request data");
            yield return Case("EMBEDIO009", "public sealed class Thing { }\npublic sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get([QueryField] Thing x) { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "unconvertible field");
            yield return Case("EMBEDIO009", "public sealed class Thing { }\npublic sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/{x}\")] public string Get(Thing x) { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "unconvertible route parameter");
            yield return Case("EMBEDIO009", "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get([QueryField] int[,] x) { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "multi-dimensional array");
            yield return Case("EMBEDIO009", "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get(ReadOnlySpan<char> x) { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "ref struct parameter");
            yield return Case("EMBEDIO009", "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get([QueryField(\"\")] string x) { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "empty field name");
            yield return Case("EMBEDIO009", "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get([QueryData] int x) { return \"\"; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "unsuitable request data");
            yield return Case("EMBEDIO011", "public sealed class C : WebApiController { private int _v; [Route(HttpVerbs.Get, \"/\")] public ref int Get() { return ref _v; } }\n[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", "ref return");
        }

        [TestCaseSource(nameof(Diagnostics))]
        public void UnsupportedDeclarationsReportErrors(string id, string source)
        {
            var result = Run(source);
            Assert.That(result.GeneratorDiagnostics.Select(d => d.Id), Does.Contain(id), string.Join("\n", result.GeneratorDiagnostics));
            Assert.That(result.GeneratorDiagnostics.Where(d => d.Id == id).Select(d => d.Severity), Is.All.EqualTo(DiagnosticSeverity.Error));
            Assert.That(result.Generated, Is.Empty);
        }

        [Test]
        public void IgnoredMethodsAndAwaitablesWarnButStillGenerate()
        {
            var result = Run(Usings + @"
public sealed class C : WebApiController
{
    [Route(HttpVerbs.Get, ""/"")] public string Get() { return """"; }
    [Route(HttpVerbs.Get, ""/private"")] private string Hidden() { return """"; }
    [Route(HttpVerbs.Get, ""/static"")] public static string Static() { return """"; }
    [Route(HttpVerbs.Get, ""/generic"")] public string Generic<T>() { return """"; }
    [Route(HttpVerbs.Get, ""/value-task"")] public ValueTask<int> Later() { return new ValueTask<int>(1); }
}
[GeneratedControllerBinding(typeof(C))] internal static partial class B { }");

            Assert.That(result.GeneratorDiagnostics.Where(d => d.Id == "EMBEDIO008"), Has.Exactly(3).Items);
            Assert.That(result.GeneratorDiagnostics.Where(d => d.Id == "EMBEDIO012"), Has.Exactly(1).Items);
            Assert.That(result.GeneratorDiagnostics.Select(d => d.Severity), Is.All.EqualTo(DiagnosticSeverity.Warning));
            Assert.That(result.Generated, Does.Contain("\"/value-task\"").And.Not.Contain("\"/private\"").And.Not.Contain("\"/generic\""));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DefaultJsonDataWarnsOnlyWhenTrimAnalyzed(bool trimAnalyzed)
        {
            var source = Usings + @"
public sealed class P { public string Name { get; set; } }
public sealed class C : WebApiController { [Route(HttpVerbs.Post, ""/"")] public string Post([JsonData] P p) { return """"; } }
[GeneratedControllerBinding(typeof(C))] internal static partial class B { }";

            var result = Run(source, trimAnalyzed: trimAnalyzed);
            Assert.That(result.GeneratorDiagnostics.Select(d => d.Id), trimAnalyzed ? Does.Contain("EMBEDIO010") : Is.Empty);
            Assert.That(result.Generated, Is.Not.Empty);
        }

        [Test]
        public void UnconvertibleParametersOutsideRoutesReceiveDefaults()
        {
            var result = Run(Usings + @"
public sealed class Thing { }
public sealed class C : WebApiController { [Route(HttpVerbs.Get, ""/{id}"")] public string Get(int id, Thing thing, object other) { return """"; } }
[GeneratedControllerBinding(typeof(C))] internal static partial class B { }");

            Assert.That(result.GeneratorDiagnostics, Is.Empty);
            Assert.That(result.Generated, Does.Contain("global::Thing __arg1 = default(global::Thing);"));
        }

        // Each converter the generator constructs is the one TypeDescriptor returns for the type,
        // so generated and reflection-based conversion use the same parser.
        [Test]
        public void GeneratedConvertersMatchTypeDescriptor()
        {
            var types = new[]
            {
                typeof(bool), typeof(byte), typeof(sbyte), typeof(char), typeof(short), typeof(ushort), typeof(int), typeof(uint),
                typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset),
                typeof(TimeSpan), typeof(Guid), typeof(Uri), typeof(Version), typeof(DateOnly), typeof(TimeOnly), typeof(Int128),
                typeof(UInt128), typeof(Half), typeof(Issue138Point),
            };
            var parameters = string.Join(", ", types.Select((t, i) => "[QueryField] " + (t == typeof(Issue138Point) ? "EmbedIO.Tests.Issues.Issue138Point" : t.FullName) + " p" + i));
            var result = Run(Usings + "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get(" + parameters + ") { return \"\"; } }\n"
                + "[GeneratedControllerBinding(typeof(C))] internal static partial class B { }");

            Assert.That(result.GeneratorDiagnostics, Is.Empty, string.Join("\n", result.GeneratorDiagnostics));
            var fields = Regex.Matches(result.Generated, @"private static readonly (?<type>[\w:.]+) (?<name>__EmbedIO_Converter\d+)")
                .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["type"].Value.Replace("global::", string.Empty, StringComparison.Ordinal));
            for (var i = 0; i < types.Length; i++)
            {
                var call = Regex.Match(result.Generated, @"__arg" + i + @" = [^;]*?, (?<field>__EmbedIO_Converter\d+)\)");
                Assert.That(call.Success, Is.True, types[i].Name);
                Assert.That(fields[call.Groups["field"].Value], Is.EqualTo(TypeDescriptor.GetConverter(types[i]).GetType().FullName), types[i].Name);
            }
        }

        // The pipeline's model compares by value, so an unrelated edit reuses the previous output.
        [Test]
        public void UnrelatedEditsReuseTheGeneratedOutput()
        {
            var parse = new CSharpParseOptions(LanguageVersion.Latest);
            var compilation = Compile(Usings + "public sealed class C : WebApiController { [Route(HttpVerbs.Get, \"/\")] public string Get() { return \"\"; } }\n"
                + "[GeneratedControllerBinding(typeof(C))] internal static partial class B { }", parse);
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new ControllerBindingGenerator().AsSourceGenerator() },
                parseOptions: parse,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
            driver = driver.RunGenerators(compilation);
            driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("internal static class Unrelated { }", parse)));

            var outputs = driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(step => step.Outputs).ToList();
            Assert.That(outputs, Is.Not.Empty);
            Assert.That(outputs.Select(o => o.Reason), Is.All.EqualTo(IncrementalStepRunReason.Cached).Or.EqualTo(IncrementalStepRunReason.Unchanged));
        }

        private static GeneratorResult Run(string source, LanguageVersion language = LanguageVersion.Latest, bool trimAnalyzed = false)
        {
            var parse = new CSharpParseOptions(language);
            var compilation = Compile(source, parse);
            var driver = CSharpGeneratorDriver.Create(
                new[] { new ControllerBindingGenerator().AsSourceGenerator() },
                parseOptions: parse,
                optionsProvider: new OptionsProvider(trimAnalyzed));
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
            var generated = string.Concat(output.SyntaxTrees.Skip(1).Select(tree => tree.ToString()));
            return new GeneratorResult(generatorDiagnostics, output.GetDiagnostics(), generated);
        }

        private static CSharpCompilation Compile(string source, CSharpParseOptions parse)
            => CSharpCompilation.Create(
                "GeneratorProbe",
                new[] { CSharpSyntaxTree.ParseText(source, parse) },
                References.Value,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: parse.LanguageVersion >= LanguageVersion.CSharp8 ? NullableContextOptions.Enable : NullableContextOptions.Disable));

        private sealed record GeneratorResult(ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompilationDiagnostics, string Generated);

        private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
        {
            private readonly Options _global;

            public OptionsProvider(bool trimAnalyzed)
            {
                _global = new Options(trimAnalyzed);
            }

            public override AnalyzerConfigOptions GlobalOptions => _global;

            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;
        }

        private sealed class Options : AnalyzerConfigOptions
        {
            internal static readonly Options Empty = new(false);

            private readonly bool _trimAnalyzed;

            public Options(bool trimAnalyzed)
            {
                _trimAnalyzed = trimAnalyzed;
            }

            public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
            {
                value = _trimAnalyzed && key == "build_property.EnableAotAnalyzer" ? "true" : null;
                return value != null;
            }
        }
    }
}
