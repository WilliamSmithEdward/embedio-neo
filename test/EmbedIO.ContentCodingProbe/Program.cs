using System.IO.Compression;
using System.Text;
using System.Text.Json;

if (args.Length != 1)
    throw new ArgumentException("Supply one output directory for the corpus and observations.");
Directory.CreateDirectory(args[0]);
var body = Encoding.UTF8.GetBytes("HTTP content-coding envelope audit");
byte[] Encode(bool zlib)
{
    using var output = new MemoryStream();
    using (Stream encoder = zlib
        ? new ZLibStream(output, CompressionLevel.SmallestSize, true)
        : new DeflateStream(output, CompressionLevel.SmallestSize, true))
        encoder.Write(body);
    return output.ToArray();
}
byte[] Decode(byte[] input, bool zlib)
{
    using var source = new MemoryStream(input);
    using Stream decoder = zlib
        ? new ZLibStream(source, CompressionMode.Decompress)
        : new DeflateStream(source, CompressionMode.Decompress);
    using var result = new MemoryStream();
    decoder.CopyTo(result);
    return result.ToArray();
}

// Non-final stored block, ignored padding, LEN=156, NLEN=~156; final empty stored block.
var ambiguous = new byte[166];
ambiguous[0] = 0x78;
ambiguous[1] = 0x9c;
ambiguous[3] = 0x63;
ambiguous[4] = 0xff;
Array.Fill(ambiguous, (byte)0x41, 5, 156);
ambiguous[161] = 1;
ambiguous[164] = 0xff;
ambiguous[165] = 0xff;
var observations = new List<object>();
var corpus = new[]
{
    (Name: "raw", Bytes: Encode(false), Expected: body),
    (Name: "zlib", Bytes: Encode(true), Expected: body),
    (Name: "raw-with-zlib-header-prefix", Bytes: ambiguous,
        Expected: Enumerable.Repeat((byte)0x41, 156).ToArray())
};
foreach (var item in corpus)
{
    File.WriteAllBytes(Path.Combine(args[0], item.Name + ".bin"), item.Bytes);
    foreach (var zlib in new[] { false, true })
    {
        try
        {
            var decoded = Decode(item.Bytes, zlib);
            var match = decoded.SequenceEqual(item.Expected);
            observations.Add(new
            {
                input = item.Name,
                decoder = zlib ? "ZLibStream" : "DeflateStream",
                prefix = Convert.ToHexString(item.Bytes.AsSpan(0, 2)),
                matchesExpected = match,
                decodedBytes = decoded.Length
            });
            if ((item.Name == "zlib") == zlib && !match)
                throw new InvalidOperationException("Expected format did not decode.");
        }
        catch (InvalidDataException ex)
        {
            observations.Add(new { input = item.Name, decoder = zlib ? "ZLibStream" : "DeflateStream", error = ex.GetType().Name });
            if ((item.Name == "zlib") == zlib) throw;
        }
    }
}
var zlibBody = Encode(true);
foreach (var mutation in new[] { "missing-checksum", "partial-checksum", "bad-checksum", "trailing-byte" })
{
    var wire = mutation switch
    {
        "missing-checksum" => zlibBody[..^4],
        "partial-checksum" => zlibBody[..^1],
        "trailing-byte" => zlibBody.Concat(new byte[] { 0x42 }).ToArray(),
        _ => zlibBody.ToArray()
    };
    if (mutation == "bad-checksum") wire[^1] ^= 1;
    File.WriteAllBytes(Path.Combine(args[0], mutation + ".bin"), wire);
    try
    {
        var output = Decode(wire, true);
        observations.Add(new { input = mutation, decoder = "ZLibStream", matchesExpected = output.SequenceEqual(body), decodedBytes = output.Length });
    }
    catch (InvalidDataException ex)
    {
        observations.Add(new { input = mutation, decoder = "ZLibStream", error = ex.GetType().Name });
    }
}
var json = JsonSerializer.Serialize(new
{
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    observations
}, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(args[0], "observations.json"), json);
Console.WriteLine(json);
