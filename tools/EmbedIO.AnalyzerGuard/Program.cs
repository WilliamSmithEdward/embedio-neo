using System.Diagnostics;
using System.Globalization;
using System.Resources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EmbedIO.AnalyzerGuard
{
    internal static class Program
    {
        private static readonly CSharpParseOptions Options = new(LanguageVersion.Preview);

        private static int Main(string[] args)
        {
            VerifyParser();
            var root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not inspect repository files.");
            var files = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries);
            process.WaitForExit();
            if (process.ExitCode != 0) return process.ExitCode;
            var count = 0;
            foreach (var file in files.Distinct(StringComparer.Ordinal).Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var path = Path.Combine(root, file);
                if (!File.Exists(path)) continue;
                var source = File.ReadAllText(path);
                foreach (var offset in FindSuppressions(source).Distinct().Order())
                {
                    var line = source.AsSpan(0, offset).Count('\n') + 1;
                    Console.Error.WriteLine($"{file}:{line}: null-forgiving operator is not permitted.");
                    count++;
                }
            }
            if (count != 0) return 1;
            Console.WriteLine(new ResourceManager("EmbedIO.AnalyzerGuard.Messages", typeof(Program).Assembly).GetString("Passed", CultureInfo.InvariantCulture));
            return 0;
        }

        private static IEnumerable<int> FindSuppressions(string source, int baseOffset = 0)
        {
            var root = CSharpSyntaxTree.ParseText(source, Options).GetRoot();
            foreach (var expression in root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
                .Where(expression => expression.IsKind(SyntaxKind.SuppressNullableWarningExpression)))
                yield return baseOffset + expression.OperatorToken.SpanStart;

            // Inactive preprocessor branches are trivia, so parse each branch separately.
            foreach (var trivia in root.DescendantTrivia().Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
                foreach (var offset in FindSuppressions(trivia.ToFullString(), baseOffset + trivia.SpanStart))
                    yield return offset;
        }

        private static void VerifyParser()
        {
            var rejected = new[]
            {
                "class C { object S = null!; }",
                "class C { object F(object x) => x!; }",
                "class C { string F(object x) => $\"{x!.ToString()}\"; }",
                "#if NEVER\nclass C { object S = null!; }\n#endif",
                "#if NEVER\nclass C { object S = null!; }\n#else\nclass C { object S = null!; }\n#endif",
                "#if OUTER\n#if INNER\nclass C { object S = null!; }\n#endif\n#endif",
                "return value!;",
                "object value = null!;",
                "class C { object F() => default!; }",
            };
            foreach (var source in rejected)
                if (!FindSuppressions(source).Any()) throw new InvalidOperationException("The C# suppression guard missed a regression fixture.");
            const string bothBranches = "class C { object F() {\n#if NEVER\nreturn value!;\n#else\nreturn value!;\n#endif\n} }";
            if (FindSuppressions(bothBranches).Distinct().Count() != 2)
                throw new InvalidOperationException("Both active and inactive method branches must be checked.");
            var accepted = new[] { "class C { bool F(bool x) => !x; }", "class C { string S = \"null!\"; }", "// null!\nclass C {}" };
            foreach (var source in accepted)
                if (FindSuppressions(source).Any()) throw new InvalidOperationException("The C# suppression guard rejected valid syntax.");
        }
    }
}
