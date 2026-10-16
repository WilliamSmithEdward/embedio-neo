using System;
using System.IO;
using System.Linq;
using System.Text;
using EmbedIO.Utilities;
using SharpFuzz;

// The driver supplies arbitrary bytes. UTF-8 replacement decoding is intentional:
// these public APIs accept strings, including malformed URL escapes.
if (args.Length == 2 && args[0] == "--multipart-corpus")
{
    var paths = Directory.GetFiles(args[1]).OrderBy(path => path, StringComparer.Ordinal);
    foreach (var path in paths)
    {
        MultipartFuzzChecks.Check(File.ReadAllBytes(path));
        Console.WriteLine("Passed multipart seed: " + Path.GetFileName(path));
    }
}
else if (args.Length == 1 && args[0] == "--afl")
{
    Fuzzer.OutOfProcess.Run((Stream stream) =>
    {
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        CheckInput(bytes.ToArray());
    });
}
else
{
    Fuzzer.LibFuzzer.Run(CheckInput);
}

static void CheckInput(ReadOnlySpan<byte> data)
{
    MultipartFuzzChecks.Check(data);
    var input = Encoding.UTF8.GetString(data);
    foreach (var groupFlags in new[] { false, true })
    {
        var mutable = UrlEncodedDataParser.Parse(input, groupFlags);
        var immutable = UrlEncodedDataParser.Parse(input, groupFlags, false);
        if (!mutable.AllKeys.SequenceEqual(immutable.AllKeys))
            throw new InvalidOperationException("Mutability changed query keys or their order.");
        foreach (var key in mutable.AllKeys)
        {
            if (!(mutable.GetValues(key) ?? Array.Empty<string>()).SequenceEqual(immutable.GetValues(key) ?? Array.Empty<string>()))
                throw new InvalidOperationException("Mutability changed query values or their order.");
        }
    }

    // Prefixing '/' satisfies the documented path precondition for every input.
    var path = "/" + input;
    foreach (var isBasePath in new[] { false, true })
    {
        var expected = new StringBuilder();
        foreach (var character in path)
        {
            if (character != '/' || expected.Length == 0 || expected[expected.Length - 1] != '/')
                expected.Append(character);
        }
        if (isBasePath && expected[expected.Length - 1] != '/')
            expected.Append('/');
        else if (!isBasePath && expected.Length > 1 && expected[expected.Length - 1] == '/')
            expected.Length--;

        var actual = UrlPath.Normalize(path, isBasePath);
        if (actual != expected.ToString() || UrlPath.Normalize(actual, isBasePath) != actual)
            throw new InvalidOperationException("Path normalization differs from the slash-collapse oracle.");
    }
}
