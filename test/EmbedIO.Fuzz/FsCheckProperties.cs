using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbedIO.Utilities;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;

internal static class FsCheckProperties
{
    internal static bool Run(string[] args)
    {
        if (args.Length == 0 || args[0] != "--fscheck") return false;
        if (args.Length > 3) throw new ArgumentException("Usage: --fscheck [seed] [cases-per-property]");
        var seed = args.Length > 1 ? ulong.Parse(args[1], CultureInfo.InvariantCulture) : 1729UL;
        var count = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 10000;
        if (count < 1 || count > 100000) throw new ArgumentOutOfRangeException(nameof(args));

        // Include malformed escapes, separators, controls and Unicode, as well as
        // ordinary text. Generated inputs stay bounded; shrinking only removes text.
        var characters = Gen.Elements("%&=/?[]#+\0\r\n\t abcXYZ0123456789é€".ToCharArray());
        var strings = Gen.Choose(0, 128).SelectMany(length => characters.ArrayOf(length))
            .Select(chars => new string(chars));
        var inputs = Arb.From(strings, Shrink);
        var config = Config.QuickThrowOnFailure.WithMaxTest(count)
            .WithReplay(FSharpOption<Replay>.Some(new Replay(new Rnd(seed), FSharpOption<int>.None)));
        Console.WriteLine($"FsCheck: seed={seed}, cases-per-property={count}, max-input-length=128");

        foreach (var basePath in new[] { false, true })
        {
            Check.One(config.WithName($"URL normalization (basePath={basePath})"),
                Prop.ForAll(inputs, input =>
                {
                    var normalized = UrlPath.Normalize("/" + input, basePath);
                    return UrlPath.IsValid(normalized)
                        && UrlPath.Normalize(normalized, basePath) == normalized;
                }));
        }

        foreach (var groupFlags in new[] { false, true })
        {
            Check.One(config.WithName($"Query mutability parity (groupFlags={groupFlags})"),
                Prop.ForAll(inputs, input =>
                {
                    var mutable = UrlEncodedDataParser.Parse(input, groupFlags);
                    var immutable = UrlEncodedDataParser.Parse(input, groupFlags, false);
                    return mutable.AllKeys.SequenceEqual(immutable.AllKeys)
                        && mutable.AllKeys.All(key =>
                            (mutable.GetValues(key) ?? Array.Empty<string>()).SequenceEqual(
                                immutable.GetValues(key) ?? Array.Empty<string>()));
                }));
        }

        Check.One(config.WithName("Multipart binary round trip and fragmentation"),
            Prop.ForAll(inputs, input => MultipartFuzzChecks.CheckText(input)));

        return true;
    }

    private static IEnumerable<string> Shrink(string input)
    {
        // Try large deletions first, then individual characters. Every candidate
        // is strictly shorter, so shrinking terminates and preserves the alphabet.
        for (var length = input.Length; length > 0; length /= 2)
            for (var start = 0; start + length <= input.Length; start += length)
                yield return input.Remove(start, length);
    }
}
