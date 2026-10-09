using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EmbedIO.Utilities;

if (HpackInterop.Run(args) || Http1HeadFuzz.Run(args) || await Http2TransportFuzz.RunAsync(args) || await Http3TransportFuzz.RunAsync(args) || await WebSocketFuzz.RunAsync(args)) return;

// Dependency-free mutation/property harness. Its scope is URL paths and query
// data; it does not claim to fuzz the listener or WebSocket frame parser.
var seed = args.Length > 0 ? int.Parse(args[0]) : 1729;
var iterations = args.Length > 1 ? int.Parse(args[1]) : 100_000;
if (iterations < 1 || iterations > 1_000_000)
    throw new ArgumentOutOfRangeException(nameof(iterations));
var random = new Random(seed);
var corpus = new List<string>(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "corpus.txt")));
const string alphabet = "%&=/?[]#+\0\r\n\t abcXYZ0123456789é€";
string input = string.Empty;
try
{
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        input = corpus[random.Next(corpus.Count)];
        for (var mutation = random.Next(1, 12); mutation > 0; mutation--)
        {
            var position = random.Next(input.Length + 1);
            if (position < input.Length && random.Next(3) == 0)
                input = input.Remove(position, 1);
            else
                input = input.Insert(position, alphabet[random.Next(alphabet.Length)].ToString());
        }
        if (input.Length > 1024)
            input = input.Substring(0, 1024);
        foreach (var groupFlags in new[] { false, true })
        {
            var mutable = UrlEncodedDataParser.Parse(input, groupFlags);
            var immutable = UrlEncodedDataParser.Parse(input, groupFlags, false);
            if (mutable.Count != immutable.Count)
                throw new InvalidOperationException("Mutability changed parsed key count.");
            foreach (var key in mutable.AllKeys)
            {
                if (mutable[key] != immutable[key])
                    throw new InvalidOperationException("Mutability changed parsed values.");
            }
        }
        var path = "/" + input;
        foreach (var basePath in new[] { false, true })
        {
            var normalized = UrlPath.Normalize(path, basePath);
            if (!UrlPath.IsValid(normalized) || UrlPath.Normalize(normalized, basePath) != normalized)
                throw new InvalidOperationException("Path normalization is not valid and idempotent.");
        }
        if (iteration % 1000 == 0 && corpus.Count < 1000)
            corpus.Add(input);
    }
    Console.WriteLine($"Passed {iterations} mutations; seed {seed}.");
}
catch (Exception)
{
    Console.Error.WriteLine($"Seed {seed}; failing input: {JsonSerializer.Serialize(input)}");
    throw;
}
