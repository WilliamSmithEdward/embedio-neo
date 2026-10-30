internal sealed record CompatibilityReport(string Engine, string Group, int Passed, IReadOnlyList<string> Cases);
