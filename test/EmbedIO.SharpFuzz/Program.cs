using System;
using System.Linq;
using System.Text;
using EmbedIO.Utilities;
using SharpFuzz;

// The driver supplies arbitrary bytes. UTF-8 replacement decoding is intentional:
// these public APIs accept strings, including malformed URL escapes.
Fuzzer.LibFuzzer.Run(data =>
{
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
});
