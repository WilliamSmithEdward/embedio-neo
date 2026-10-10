using System.ComponentModel;
using System.Globalization;

internal sealed class CommandLine(string[] args)
{
    internal bool Has(string name) => args.Contains(name, StringComparer.Ordinal);

    internal string? Optional(string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        return index + 1 < args.Length ? args[index + 1] : throw new ArgumentException(name + " requires a value.");
    }

    internal string Required(string name) => Optional(name) ?? throw new ArgumentException(name + " is required.");

    internal string Text(string name, string fallback) => Optional(name) ?? fallback;

    internal int Integer(string name, int fallback)
        => Optional(name) is { } text ? int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : fallback;

    internal double Number(string name, double fallback)
        => Optional(name) is { } text ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;
}

// Line protocol tokens exchanged between the orchestrator and its child processes.
internal static class Control
{
    internal static void Write([Localizable(false)] string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    internal static async Task ExpectAsync([Localizable(false)] string token, CancellationToken cancellation)
    {
        var line = await Console.In.ReadLineAsync(cancellation).ConfigureAwait(false);
        if (line != token) throw new InvalidOperationException($"Expected control token '{token}', received '{line}'.");
    }
}
