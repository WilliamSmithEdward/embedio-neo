using System.ComponentModel;

// These lines are protocol tokens consumed by the benchmark coordinator.
// They must remain invariant across process cultures.
internal static class BenchmarkControl
{
    internal static void Write([Localizable(false)] string token) => Console.WriteLine(token);
}
