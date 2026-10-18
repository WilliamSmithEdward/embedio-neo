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
        path, category, diagnostic, reason, _ = COMPATIBILITY_SUPPRESSIONS[0]
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
