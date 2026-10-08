using System.Reflection;
using System.Text.Json;
using System.Runtime.ExceptionServices;
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var encoder = assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoder", true) ?? throw new Exception("Missing encoder");
var encode = encoder.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new Exception("Missing encoding method");
var hidden = BindingFlags.Instance | BindingFlags.NonPublic;
var type = assembly.GetType("EmbedIO.Net.Internal.Http3.QpackDecoder", true) ?? throw new Exception("Missing decoder");
var decoder = Activator.CreateInstance(type, hidden, null, new object[] { int.Parse(args[1]), 16, 65536, 65536, 1048576L, 65536 }, null) ?? throw new Exception("Missing decoder instance");
using var owned = (IDisposable)decoder;
object? Invoke(string name, params object[] values)
{
    try { return (type.GetMethod(name, hidden) ?? throw new Exception(name)).Invoke(decoder, values); }
    catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
}
object? Property(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
object Headers(Array fields) => fields.Cast<object>().Select(f => new[] { Property(f, "Name"), Property(f, "Value") }).ToArray();
object Completed(long stream, Array fields)
{
    var wire = (byte[]?)encode.Invoke(null, new object[] { fields, 65536, 65536 }) ?? throw new Exception("Missing encoded output");
    return new { stream, fields = Headers(fields), wire = Convert.ToHexString(wire) };
}
string? line;
while ((line = Console.ReadLine()) != null)
{
    using var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;
    var operation = root.GetProperty("op").GetString();
    var ready = new List<object>();
    var blocked = false;
    if (operation == "submit")
    {
        var stream = root.GetProperty("stream").GetInt64();
        var fields = (Array?)Invoke("Submit", stream, Convert.FromHexString(root.GetProperty("wire").GetString() ?? ""));
        if (fields == null) blocked = true;
        else ready.Add(Completed(stream, fields));
    }
    else if (operation == "feed")
    {
        var bytes = Convert.FromHexString(root.GetProperty("wire").GetString() ?? "");
        var completions = (Array?)Invoke("FeedEncoder", bytes, 0, bytes.Length) ?? throw new Exception("Missing completions");
        foreach (var completion in completions.Cast<object>())
            ready.Add(Completed((long)(Property(completion, "StreamId") ?? throw new Exception("Missing stream ID")), (Array)(Property(completion, "Fields") ?? throw new Exception("Missing fields"))));
    }
    else if (operation == "cancel") Invoke("Cancel", root.GetProperty("stream").GetInt64());
    else throw new Exception("Unknown operation");
    var feedback = (byte[]?)Invoke("DrainFeedback") ?? throw new Exception("Missing feedback");
    Console.WriteLine(JsonSerializer.Serialize(new { ready, blocked, feedback = Convert.ToHexString(feedback) }));
}
