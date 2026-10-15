using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

internal static class HpackInterop
{
    internal static bool Run(string[] args)
    {
        if (args.Length == 0 || args[0] != "--hpack-corpus") return false;
        if (args.Length != 3) throw new ArgumentException("--hpack-corpus input.jsonl output.jsonl");
        var assembly = typeof(EmbedIO.WebServer).Assembly;
        var decoderType = (assembly.GetType("EmbedIO.Net.Internal.Http2.HpackDecoder", true) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var encoderType = (assembly.GetType("EmbedIO.Net.Internal.Http2.HpackEncoder", true) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var fieldType = (assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Create(Type type) => (Activator.CreateInstance(type, flags, null, new object[] { 32768 }, null) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var decoder = Create(decoderType);
        var encoder = Create(encoderType);
        var decode = (decoderType.GetMethod("Decode", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")).CreateDelegate<Func<byte[], Array>>(decoder);
        var setDecoder = (decoderType.GetMethod("SetMaximumTableSize", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")).CreateDelegate<Action<int>>(decoder);
        var setEncoder = (encoderType.GetMethod("SetMaximumTableSize", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")).CreateDelegate<Action<int>>(encoder);
        var encode = (encoderType.GetMethod("Encode", flags) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var name = (fieldType.GetProperty("Name") ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        var value = (fieldType.GetProperty("Value") ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
        using var output = new StreamWriter(args[2]);
        var count = 0;
        foreach (var line in File.ReadLines(args[1]))
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            foreach (var size in root.GetProperty("tableSizes").EnumerateArray())
            { setDecoder(size.GetInt32()); setEncoder(size.GetInt32()); }
            var headers = root.GetProperty("headers");
            var decoded = decode(Convert.FromHexString((root.GetProperty("wire").GetString() ?? throw new System.InvalidOperationException("Expected a non-null fixture value."))));
            if (decoded.Length != headers.GetArrayLength()) throw new InvalidDataException("Header count mismatch.");
            var fields = Array.CreateInstance(fieldType, decoded.Length);
            for (var i = 0; i < decoded.Length; i++)
            {
                var expected = headers[i];
                var n = (expected.GetProperty("name").GetString() ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
                var v = (expected.GetProperty("value").GetString() ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
                if ((string)(name.GetValue(decoded.GetValue(i)) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")) != n || (string)(value.GetValue(decoded.GetValue(i)) ?? throw new System.InvalidOperationException("Expected a non-null fixture value.")) != v)
                    throw new InvalidDataException("Header mismatch at block " + count);
                fields.SetValue(Activator.CreateInstance(fieldType, flags, null, new object[] { n, v, expected.GetProperty("sensitive").GetBoolean() }, null), i);
            }
            var wire = (byte[])(encode.Invoke(encoder, new object[] { fields }) ?? throw new System.InvalidOperationException("Expected a non-null fixture value."));
            output.WriteLine(JsonSerializer.Serialize(new { wire = Convert.ToHexString(wire) }));
            count++;
        }
        Console.WriteLine("Validated independent decode and exported " + count + " encoded blocks.");
        return true;
    }
}
