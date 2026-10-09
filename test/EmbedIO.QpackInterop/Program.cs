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
var encoderFeedbackType = assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoderFeedback", true) ?? throw new Exception("Missing encoder feedback");
var encoderFeedback = Activator.CreateInstance(encoderFeedbackType, hidden, null, new object[] { 16, 64 }, null) ?? throw new Exception("Missing feedback instance");
var encoderTableType = assembly.GetType("EmbedIO.Net.Internal.Http3.QpackEncoderTable", true) ?? throw new Exception("Missing encoder table");
var encoderTable = Activator.CreateInstance(encoderTableType, hidden, null, new object[] { int.Parse(args[1]), encoderFeedback }, null) ?? throw new Exception("Missing encoder table instance");
object? EncoderCall(object target, string method, params object[] values)
{
    try { return (target.GetType().GetMethod(method, hidden) ?? throw new Exception(method)).Invoke(target, values); }
    catch (TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
}
string? line;
while ((line = Console.ReadLine()) != null)
{
    using var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;
    var operation = root.GetProperty("op").GetString();
    var ready = new List<object>();
    var blocked = false;
    object? insertion = null;
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
    else if (operation == "insert")
    {
        var name = root.GetProperty("name").GetString() ?? throw new Exception("Missing name");
        var value = root.GetProperty("value").GetString() ?? throw new Exception("Missing value");
        var field = Activator.CreateInstance(assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new Exception("Missing field"),
            hidden, null, new object[] { name, value, false }, null) ?? throw new Exception("Missing field instance");
        var added = EncoderCall(encoderTable, "TryInsert", field, 65536);
        if (added != null)
        {
            var index = (long)(Property(added, "Index") ?? throw new Exception("Missing index"));
            var stream = root.GetProperty("stream").GetInt64();
            var fieldArray = Array.CreateInstance(field.GetType(), 1);
            fieldArray.SetValue(field, 0);
            var section = encoder.GetMethod("EncodeReferenced", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null,
                new object[] { fieldArray, new[] { index }, (long)int.Parse(args[1]), 65536, 65536 }) ?? throw new Exception("Missing encoded section");
            var references = (long[])(Property(section, "References") ?? throw new Exception("Missing references"));
            if (!Convert.ToBoolean(EncoderCall(encoderFeedback, "TryRegisterSection", stream, references))) throw new Exception("Reference admission failed");
            insertion = new { index, instructions = Convert.ToHexString((byte[])(Property(added, "Instructions") ?? throw new Exception("Missing instructions"))), wire = Convert.ToHexString((byte[])(Property(section, "Wire") ?? throw new Exception("Missing wire"))) };
        }
    }
    else if (operation == "encoder-feedback")
    {
        var bytes = Convert.FromHexString(root.GetProperty("wire").GetString() ?? "");
        EncoderCall(encoderFeedback, "Feed", bytes, 0, bytes.Length);
    }
    else throw new Exception("Unknown operation");
    var feedback = (byte[]?)Invoke("DrainFeedback") ?? throw new Exception("Missing feedback");
    Console.WriteLine(JsonSerializer.Serialize(new { ready, blocked, feedback = Convert.ToHexString(feedback), insertion }));
}
