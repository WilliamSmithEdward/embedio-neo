using System;
using System.Globalization;
using System.Text;

namespace EmbedIO.PlatformTests
{
    // A deterministic JSON document of an exact length, shaped like an API listing: keys repeat,
    // values vary. Compression benchmarks use it as a dynamic response body. ASCII only, so
    // its length in bytes equals its length in characters.
    internal static class CompressibleJson
    {
        private static readonly string[] Adjectives = { "amber", "brisk", "calm", "dusty", "eager", "gentle", "hollow", "ivory", "jolly", "keen", "lunar", "misty", "noble", "olive", "proud", "quiet" };
        private static readonly string[] Nouns = { "falcon", "harbor", "lantern", "meadow", "orchard", "pebble", "quarry", "river", "summit", "thistle", "valley", "willow", "cedar", "ember", "glacier", "prairie" };
        private static readonly string[] Products = { "desk lamp", "notebook", "backpack", "kettle", "headphones", "monitor stand", "water bottle", "keyboard", "chair mat", "plant pot", "wall clock", "coffee grinder" };
        private static readonly string[] Categories = { "lighting", "stationery", "travel", "kitchen", "audio", "office", "garden", "decor" };
        private static readonly string[] Tags = { "new", "sale", "eco", "bestseller", "limited", "gift", "bundle", "refurbished", "imported", "handmade" };
        private static readonly string[] Suppliers = { "North Ridge Supply", "Harbor Lane Goods", "Blue Mesa Trading", "Red Cedar Works", "Silver Coast Imports", "Old Mill Partners" };
        private static readonly string[] Countries = { "DE", "US", "JP", "BR", "IN", "FR", "MX", "KE" };

        // {"items":[record,...],"note":"words"}: records while they fit, then words fill the note
        // so the document is exactly the requested length.
        internal static byte[] Create(int length)
        {
            const string Head = "{\"items\":[";
            const string Tail = "],\"note\":\"";
            const string End = "\"}";
            if (length < Head.Length + Tail.Length + End.Length)
                throw new ArgumentOutOfRangeException(nameof(length), "The document needs at least 22 bytes.");

            var random = new Xorshift(0x9E3779B9);
            var text = new StringBuilder(length).Append(Head);
            for (var id = 10000; ; id++)
            {
                var record = Record(id, random);
                var separator = text.Length == Head.Length ? 0 : 1;
                if (text.Length + separator + record.Length + Tail.Length + End.Length > length)
                    break;

                if (separator == 1) text.Append(',');
                text.Append(record);
            }

            text.Append(Tail);
            var noteEnd = length - End.Length;
            while (text.Length < noteEnd)
            {
                var word = Nouns[random.Next(Nouns.Length)];
                text.Append(word, 0, Math.Min(word.Length, noteEnd - text.Length));
                if (text.Length < noteEnd) text.Append(' ');
            }

            return Encoding.ASCII.GetBytes(text.Append(End).ToString());
        }

        private static string Record(int id, Xorshift random)
        {
            var adjective = Adjectives[random.Next(Adjectives.Length)];
            var name = char.ToUpperInvariant(adjective[0]) + adjective.Substring(1) + " " + Nouns[random.Next(Nouns.Length)] + " " + Products[random.Next(Products.Length)];
            var tagCount = 1 + random.Next(3);
            var tags = new StringBuilder();
            for (var i = 0; i < tagCount; i++)
                tags.Append(i == 0 ? "\"" : ",\"").Append(Tags[random.Next(Tags.Length)]).Append('"');

            var updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(random.Next(365 * 24 * 3600));
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"id\":{0},\"sku\":\"{1}{2}-{3:D4}-{4}\",\"name\":\"{5}\",\"category\":\"{6}\",\"price\":{7}.{8:D2},\"stock\":{9},\"rating\":{10}.{11},\"active\":{12},\"tags\":[{13}],\"updated\":\"{14:yyyy-MM-ddTHH:mm:ssZ}\",\"supplier\":{{\"id\":{15},\"name\":\"{16}\",\"country\":\"{17}\"}}}}",
                id,
                (char)('A' + random.Next(26)),
                (char)('A' + random.Next(26)),
                random.Next(10000),
                Countries[random.Next(Countries.Length)],
                name,
                Categories[random.Next(Categories.Length)],
                1 + random.Next(500),
                random.Next(100),
                random.Next(1000),
                1 + random.Next(4),
                random.Next(10),
                random.Next(4) == 0 ? "false" : "true",
                tags,
                updated,
                100 + random.Next(900),
                Suppliers[random.Next(Suppliers.Length)],
                Countries[random.Next(Countries.Length)]);
        }

        // A fixed-seed generator, so every run and every engine serves the same bytes.
        private sealed class Xorshift
        {
            private uint _state;

            internal Xorshift(uint seed) => _state = seed;

            internal int Next(int bound)
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return (int)(_state % (uint)bound);
            }
        }
    }
}
