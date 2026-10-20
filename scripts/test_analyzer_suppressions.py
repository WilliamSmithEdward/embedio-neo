"""Regression checks for the repository's suppression guard."""
import unittest

from check_analyzer_suppressions import COMPATIBILITY_SUPPRESSIONS, violations


class SuppressionGuardTests(unittest.TestCase):
    def test_namespace_exception_is_limited_to_root_editorconfig(self):
        setting = 'dotnet_diagnostic.IDE0130.severity = none'
        self.assertFalse(violations(setting, filename='.editorconfig'))
        for filename in (None, '.globalconfig', 'src/.editorconfig', 'Example.cs'):
            with self.subTest(filename=filename):
                self.assertTrue(violations(setting, filename=filename))
        for source in ('dotnet_diagnostic.CA1031.severity = none',
                       'dotnet_diagnostic.IDE0130.severity = silent',
                       setting + '\ndotnet_diagnostic.IDE0055.severity = none'):
            with self.subTest(source=source):
                self.assertTrue(violations(source, filename='.editorconfig'))

    def test_compatibility_message_exception_requires_exact_member_and_reason(self):
        path, category, diagnostic, reason, _ = next(
            entry for entry in COMPATIBILITY_SUPPRESSIONS
            if entry[0] == 'src/EmbedIO/Tus/TusUploadOptions.cs')
        source = ('[System.Diagnostics.CodeAnalysis.SuppressMessage(\n'
                  f'    "{category}", "{diagnostic}",\n'
                  f'    Justification = "{reason}"\n)]\n'
                  'public TusUploadOptions(string directory) { }')
        self.assertFalse(violations(source, csharp=True, filename=path))
        for changed, filename in (
            (source, 'src/Other.cs'),
            (source.replace('TusUploadOptions(', 'OtherMember('), path),
            (source.replace(reason, 'Convenience'), path),
            (source.replace('CA1512', 'CA1031'), path),
            (source + '\n#pragma warning disable CA1512', path),
            (source.replace('public TusUploadOptions', '[SuppressMessage("Usage", "CA1001")]\npublic TusUploadOptions'), path),
        ):
            with self.subTest(source=changed, filename=filename):
                self.assertTrue(violations(changed, csharp=True, filename=filename))

    def test_security_header_lock_exceptions_are_member_specific(self):
        for path, member in (
            ('src/EmbedIO/Caching/OutputCache.cs', '_gate'),
            ('src/EmbedIO/Net/Internal/Multiplexed/MultiplexedResponse.cs', '_lifecycle'),
            ('src/EmbedIO/Security/SecurityHeadersState.cs', '_gate'),
        ):
            source = f"""[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0330:Use 'System.Threading.Lock'", Justification = "The shared implementation targets netstandard2.0, which has no System.Threading.Lock.")]
private readonly object {member} = new();"""
            self.assertFalse(violations(source, csharp=True, filename=path))
            self.assertTrue(violations(source.replace(member, '_other'), csharp=True, filename=path))
            self.assertTrue(violations(source, csharp=True, filename='src/Other.cs'))

    def test_stacked_compatibility_attributes_remain_member_and_diagnostic_specific(self):
        path = 'src/EmbedIO/Routing/NamedRoutes.cs'
        source = '\n'.join((
            '[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0057:Use range operator", Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index.")]',
            '[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1865:Use char overload", Justification = "The shared implementation targets netstandard2.0, which has no string.EndsWith(char).")]',
            'public string GetPath(string name, IReadOnlyDictionary<string, object?>? values = null) => name;',
        ))
        self.assertFalse(violations(source, csharp=True, filename=path))
        self.assertTrue(violations(source.replace('GetPath(', 'Other('), csharp=True, filename=path))
        self.assertTrue(violations(source, csharp=True, filename='src/Other.cs'))
        self.assertTrue(violations(source.replace('CA1865:Use char overload', 'CA1031:Do not catch general exception types'), csharp=True, filename=path))

    def test_rejects_diagnostic_and_build_opt_outs(self):
        for source in (
            '#pragma warning disable CS8602',
            '#pragma warning restore CS8602',
            '[SuppressMessage("Design", "CA1001")]',
            '[UnconditionalSuppressMessage("Trimming", "IL2026")]',
            '#nullable disable',
            'dotnet_diagnostic.CA1031.severity = none',
            'dotnet_diagnostic.CA1031.severity = silent',
            '<NoWarn>CS8602</NoWarn>',
            '<WarningsNotAsErrors>CS8602</WarningsNotAsErrors>',
            'dotnet build -p:NoWarn=CS8602',
            'csc /nowarn:CS8602',
            '<EnableNETAnalyzers>false</EnableNETAnalyzers>',
            'dotnet build -p:RunAnalyzers=false',
            '<RunAnalyzersDuringLiveAnalysis>false</RunAnalyzersDuringLiveAnalysis>',
            '<TreatWarningsAsErrors>false</TreatWarningsAsErrors>',
            '<WarningLevel>0</WarningLevel>',
            '<Nullable>disable</Nullable>',
            '<AnalysisMode>None</AnalysisMode>',
            '<AnalysisLevel>none</AnalysisLevel>',
        ):
            with self.subTest(source=source):
                self.assertTrue(violations(source))

    def test_rejects_nullable_suppression_expressions(self):
        for source in ('var x = null!;', 'return result!;', 'var x = Read()!.Name;',
                       'var x = array[0]!;', 'var x = function!(args);', 'var x = a! + b;',
                       'return default!;', 'var x = value\n!;'):
            with self.subTest(source=source):
                self.assertTrue(violations(source, csharp=True))

    def test_accepts_logic_and_literal_exclamations(self):
        for source in ('return !valid;', 'if (!valid) return;', 'var x = a != b;',
                       'var x = "null!";', 'var x = @"value!";', "var x = '!';",
                       '// value!\nreturn true;', '/* value! */ return true;',
                       'var x = $"Hello {name}!";', '<RunAnalyzers>true</RunAnalyzers>'):
            with self.subTest(source=source):
                self.assertFalse(violations(source, csharp=True))

    def test_conditional_directives_accept_logical_negation(self):
        for source in ('#if !NET10_0_OR_GREATER', '#elif !TARGET',
                       '#if !(FIRST || SECOND)', '#if FIRST && !SECOND'):
            with self.subTest(source=source):
                self.assertFalse(violations(source, csharp=True))

    def test_conditional_branch_still_rejects_nullable_suppression(self):
        source = '#if !NET10_0_OR_GREATER\nvar x = value!;\n#endif'
        self.assertEqual(violations(source, csharp=True), [(2, 'var x = value!;')])

    def test_preserves_line_numbers_after_comments(self):
        self.assertEqual(violations('/* ignored!\ntext */\nreturn value!;', csharp=True),
                         [(3, 'return value!;')])


if __name__ == '__main__':
    unittest.main()
