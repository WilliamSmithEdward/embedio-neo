"""Regression checks for the repository's suppression guard."""
import unittest

from check_analyzer_suppressions import violations


class SuppressionGuardTests(unittest.TestCase):
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

    def test_preserves_line_numbers_after_comments(self):
        self.assertEqual(violations('/* ignored!\ntext */\nreturn value!;', csharp=True),
                         [(3, 'return value!;')])


if __name__ == '__main__':
    unittest.main()
