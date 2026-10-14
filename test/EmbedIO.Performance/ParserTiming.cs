using System.Collections.Specialized;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedIO;

// --parser-timing measures the parsers restructured in the complexity pass: JSON
// input normalization, byte-range selection, Priority dictionaries, chunked request
// bodies, DEFLATE framing validation and HTTP/3 control frames. Run the same build
// against a baseline and a candidate EmbedIO.dll (copy the output and replace only
// the core) and compare ns/op and B/op; every operation validates its result.
internal static class ParserTiming
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--parser-timing", StringComparer.Ordinal)) return false;
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
        var assembly = typeof(WebServer).Assembly;
        Console.WriteLine($"core: {assembly.Location}; sha256={Hash(assembly.Location)}");
        MeasureJson(assembly);
        MeasureRanges();
        MeasurePriority(assembly);
        MeasureChunked(assembly);
        MeasureDeflate(assembly);
        MeasureControl(assembly);
        return true;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // Runs the operation for at least half a second before the shared measurement so
    // tiered compilation and dynamic PGO have settled under the runtime's defaults.
    private static void Measure(string name, Func<int> operation, int expected)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(500)) _ = operation();
        AuditAllocations.Measure(name, operation, expected, null);
    }

    private static void MeasureJson(Assembly assembly)
    {
        var type = assembly.GetType("EmbedIO.Serialization.CompatibleJsonInput", true) ?? throw new InvalidOperationException("Missing JSON normalizer.");
        var normalize = (type.GetMethod("Normalize", BindingFlags.Static | BindingFlags.Public) ?? throw new InvalidOperationException("Missing Normalize."))
            .CreateDelegate<Func<string, JsonCommentHandling, string>>();
        var filler = new string('x', 512);
        var plain = "{\"id\":12345,\"name\":\"Neo bench\",\"tags\":[\"a\",\"b\",\"c\"],\"nested\":{\"value\":1.5,\"enabled\":true,\"items\":[1,2,3,4,5,6,7,8,9,10]},\"text\":\"" + filler + "\"}";
        var legacy = "{\"id\":+12345,\"name\":\"Neo\tbench\",\"ratio\":.5,\"count\":007,\"end\":5.,\"items\":[1,2,3] // trailing\n,\"text\":\"" + filler + "\"}";
        var expected = normalize(legacy, JsonCommentHandling.Skip).Length;
        Measure("json-normalize-plain-" + plain.Length, () => ReferenceEquals(normalize(plain, JsonCommentHandling.Skip), plain) ? plain.Length : -1, plain.Length);
        Measure("json-normalize-legacy-" + legacy.Length, () => normalize(legacy, JsonCommentHandling.Skip).Length, expected);
    }

    private static void MeasureRanges()
    {
        var select = typeof(HttpRequestExtensions).GetMethod("SelectByteRanges", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing SelectByteRanges.");
        var request = Expression.Parameter(typeof(IHttpRequest));
        var selectRanges = Expression.Lambda<Func<IHttpRequest, Array?>>(
            Expression.Convert(Expression.Call(select, request, Expression.Constant(1000L), Expression.Constant(null, typeof(string)),
                Expression.Constant(null, typeof(DateTimeOffset?)), Expression.Constant(false), Expression.Constant(8)), typeof(Array)), request).Compile();
        var two = new RangeRequest("bytes=0-99, 200-299");
        Measure("range-select-two", () => selectRanges(two)?.Length ?? -1, 2);
        var open = new RangeRequest("bytes=500-");
        Measure("range-single-open", () => open.TryGetByteRange(1000, null, null, out var start, out var length) ? (int)(start + length) : -1, 1000);
    }

    private static void MeasurePriority(Assembly assembly)
    {
        var type = assembly.GetType("EmbedIO.Net.Internal.HttpStructuredFields", true) ?? throw new InvalidOperationException("Missing structured fields.");
        var method = type.GetMethod("TryParsePriority", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing TryParsePriority.");
        var priorityType = assembly.GetType("EmbedIO.Net.Internal.HttpPriority", true) ?? throw new InvalidOperationException("Missing priority type.");
        var field = Expression.Parameter(typeof(string));
        var priority = Expression.Variable(priorityType);
        var parse = Expression.Lambda<Func<string, int>>(
            Expression.Block(new[] { priority },
                Expression.Condition(Expression.Call(method, field, priority), Expression.Property(priority, "Urgency"), Expression.Constant(-1))),
            field).Compile();
        Measure("priority-dictionary", () => parse("u=3, i; x=1"), 3);
    }

    private static void MeasureChunked(Assembly assembly)
    {
        var type = assembly.GetType("EmbedIO.Net.Internal.ChunkedRequestStream", true) ?? throw new InvalidOperationException("Missing chunked stream.");
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(Stream), typeof(byte[]), typeof(int), typeof(int) }, null) ?? throw new InvalidOperationException("Missing chunked constructor.");
        var input = Expression.Parameter(typeof(byte[]));
        var create = Expression.Lambda<Func<byte[], Stream>>(Expression.New(constructor,
            Expression.Constant(Stream.Null, typeof(Stream)), input, Expression.Constant(0), Expression.ArrayLength(input)), input).Compile();
        var wire = new StringBuilder();
        for (var i = 0; i < 64; i++) wire.Append("10\r\n").Append('x', 16).Append("\r\n");
        wire.Append("0\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(wire.ToString());
        var destination = new byte[4096];
        Measure("chunked-64x16", () =>
        {
            using var stream = create(bytes);
            var total = 0;
            int read;
            while ((read = stream.Read(destination, 0, destination.Length)) != 0) total += read;
            return total;
        }, 1024);
    }

    private static void MeasureDeflate(Assembly assembly)
    {
        var type = assembly.GetType("EmbedIO.Internal.DeflateFramingValidator", true) ?? throw new InvalidOperationException("Missing DEFLATE validator.");
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null) ?? throw new InvalidOperationException("Missing validator constructor.");
        var feed = type.GetMethod("Feed", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing Feed.");
        var decoded = type.GetProperty("DecodedBytes", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing DecodedBytes.");
        var data = Expression.Parameter(typeof(byte[]));
        var validator = Expression.Variable(type);
        var validate = Expression.Lambda<Func<byte[], long>>(
            Expression.Block(new[] { validator },
                Expression.Assign(validator, Expression.New(constructor)),
                Expression.Call(validator, feed, data, Expression.Constant(0), Expression.ArrayLength(data)),
                Expression.Convert(Expression.Property(validator, decoded), typeof(long))),
            data).Compile();
        var payload = new byte[2048];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 7 == 0 ? 'A' : 'a' + i % 13);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true)) deflate.Write(payload, 0, payload.Length);
        var compressed = output.ToArray();
        Measure("deflate-validate-2k-" + compressed.Length, () => (int)validate(compressed), payload.Length);
    }

    private static void MeasureControl(Assembly assembly)
    {
        var type = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3ControlStream", true) ?? throw new InvalidOperationException("Missing control stream.");
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Stream), typeof(bool) }, null)
            ?? throw new InvalidOperationException("Missing control stream constructor.");
        var read = type.GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing ReadAsync.");
        var source = Expression.Parameter(typeof(Stream));
        var create = Expression.Lambda<Func<Stream, object>>(Expression.New(constructor, source, Expression.Constant(false)), source).Compile();
        var instance = Expression.Parameter(typeof(object));
        var task = Expression.Call(Expression.Convert(instance, type), read, Expression.Constant(CancellationToken.None));
        var awaiter = Expression.Call(task, task.Type.GetMethod("GetAwaiter") ?? throw new InvalidOperationException("Missing GetAwaiter."));
        var result = Expression.Call(awaiter, awaiter.Type.GetMethod("GetResult") ?? throw new InvalidOperationException("Missing GetResult."));
        var readType = Expression.Lambda<Func<object, long>>(Expression.Property(result, "Type"), instance).Compile();
        var wire = new List<byte>();
        Frame(wire, 4, Varint(1).Concat(Varint(4096)).Concat(Varint(7)).Concat(Varint(100)).ToArray());
        for (var i = 0; i < 8; i++) Frame(wire, 0xf0700, Varint(4 * i).Concat(Encoding.ASCII.GetBytes("u=3,i")).ToArray());
        var bytes = wire.ToArray();
        Measure("http3-control-settings-8-priority", () =>
        {
            using var memory = new MemoryStream(bytes);
            var stream = create(memory);
            var events = 0;
            for (var i = 0; i < 9; i++)
                if (readType(stream) is 4 or 0xf0700) events++;
            return events;
        }, 9);
    }

    private static void Frame(List<byte> wire, long type, byte[] payload)
    {
        wire.AddRange(Varint(type));
        wire.AddRange(Varint(payload.Length));
        wire.AddRange(payload);
    }

    private static byte[] Varint(long value)
    {
        if (value < 64) return new[] { (byte)value };
        if (value < 16384) return new[] { (byte)(0x40 | (value >> 8)), (byte)value };
        if (value < 1073741824) return new[] { (byte)(0x80 | (value >> 24)), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        return new[] { (byte)(0xC0 | (value >> 56)), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
    }

    // Only the members the range selectors read are populated.
    private sealed class RangeRequest : IHttpRequest
    {
        internal RangeRequest(string range) => Headers = new NameValueCollection { [HttpHeaderNames.Range] = range };

        public NameValueCollection Headers { get; }
        public string HttpMethod => "GET";
        public ICookieCollection Cookies => throw new NotSupportedException();
        public Version ProtocolVersion => HttpVersion.Version11;
        public bool KeepAlive => true;
        public string RawTarget => "/";
        public NameValueCollection QueryString => throw new NotSupportedException();
        public HttpVerbs HttpVerb => HttpVerbs.Get;
        public Uri Url => new("http://localhost/");
        public bool HasEntityBody => false;
        public Stream InputStream => Stream.Null;
        public Encoding ContentEncoding => Encoding.UTF8;
        public IPEndPoint RemoteEndPoint => new(IPAddress.Loopback, 1);
        public bool IsLocal => true;
        public bool IsSecureConnection => false;
        public string? UserAgent => null;
        public bool IsWebSocketRequest => false;
        public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 2);
        public string? ContentType => null;
        public long ContentLength64 => 0;
        public bool IsAuthenticated => false;
        public Uri? UrlReferrer => null;
    }
}
