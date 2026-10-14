using System.Collections;
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
using EmbedIO.Net;
using EmbedIO.Utilities;

// --parser-timing measures the parsers restructured in the complexity pass: JSON
// input normalization, byte-range selection, Priority dictionaries, chunked request
// bodies, DEFLATE framing validation, HTTP/3 control frames, URL-encoded data,
// request coding chains, q-value lists, request cookies, Expect members, Forwarded
// headers and QUERY format policies. Run the same build against a baseline and a candidate
// EmbedIO.dll (copy the output and replace only the core) and compare ns/op and
// B/op; every operation validates its result.
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
        MeasureResponseFields(assembly);
        MeasurePriority(assembly);
        MeasureChunked(assembly);
        MeasureDeflate(assembly);
        MeasureControl(assembly);
        MeasureUrlEncoded();
        MeasureCodingChain(assembly);
        MeasureQValues();
        MeasureRequestCookies(assembly);
        MeasureExpectations(assembly);
        MeasureForwarded(assembly);
        MeasureQueryFormat();
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

    // What the response-head validation costs per response. A baseline core has no such
    // check, so its cost is zero by construction and this measures the whole addition. The
    // copy of the same values, which the encoder performs immediately afterwards either way,
    // is measured beside it to give the number a scale.
    private static void MeasureResponseFields(Assembly assembly)
    {
        // A response EmbedIO writes itself: Vary, Content-Type, Server, Date, framing,
        // Connection, and a session cookie.
        var values = new[]
        {
            "Accept-Encoding",
            "text/plain; charset=utf-8",
            "EmbedIO/2.0.0+0a67aba81fc0cb37b53390ad550b0671f208da84",
            "Wed, 14 Oct 2026 11:31:04 GMT",
            "chunked",
            "keep-alive",
            "__session=5trkVx1rQk2hO2tAXk5Dmw; Path=/; HttpOnly",
        };

        var type = assembly.GetType("EmbedIO.Internal.HttpTokenSyntax", false);
        var method = type?.GetMethod("IsValidFieldValue", BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null)
        {
            Console.WriteLine($"response-field-validate-{values.Length}: absent from this core, so it costs nothing here");
        }
        else
        {
            var valid = method.CreateDelegate<Func<string, bool>>();
            Measure("response-field-validate-" + values.Length, () =>
            {
                var accepted = 0;
                foreach (var value in values) accepted += valid(value) ? 1 : 0;
                return accepted;
            }, values.Length);
        }

        var destination = new char[512];
        Measure("response-field-copy-" + values.Length, () =>
        {
            var position = 0;
            foreach (var value in values)
            {
                value.AsSpan().CopyTo(destination.AsSpan(position));
                position += value.Length;
            }

            return position;
        }, values.Sum(value => value.Length));
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

    private static void MeasureUrlEncoded()
    {
        var query = "?id=12345&name=Neo+bench&tag=a&tag=b&city=Z%C3%BCrich&flag&item[0]=first&item[1]=second";
        Measure("url-encoded-query-" + query.Length, () => UrlEncodedDataParser.Parse(query, false).Count, 6);
        Measure("url-encoded-grouped-" + query.Length, () => UrlEncodedDataParser.Parse(query, true).Count, 6);
    }

    private static void MeasureCodingChain(Assembly assembly)
    {
        var type = assembly.GetType("EmbedIO.Internal.RequestCodingChain", true) ?? throw new InvalidOperationException("Missing coding chain.");
        var method = type.GetMethod("TryOpen", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing TryOpen.");
        var source = Expression.Parameter(typeof(Stream));
        var decoded = Expression.Variable(typeof(Stream));
        var open = Expression.Lambda<Func<Stream, Stream?>>(
            Expression.Block(new[] { decoded },
                Expression.Condition(
                    Expression.Call(method, source, Expression.Constant("gzip, deflate"), Expression.Constant(true), decoded),
                    decoded, Expression.Constant(null, typeof(Stream)))),
            source).Compile();
        var payload = new byte[64];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + i % 26);
        var encoded = Encode(Encode(payload, "gzip"), "deflate");
        var buffer = new byte[256];
        Measure("request-coding-gzip-deflate-" + encoded.Length, () =>
        {
            using var memory = new MemoryStream(encoded, false);
            using var stream = open(memory) ?? throw new InvalidOperationException("Rejected request codings.");
            var total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) total += read;
            return total;
        }, 64);
    }

    private static byte[] Encode(byte[] body, string coding)
    {
        using var output = new MemoryStream();
        using (Stream encoder = coding == "gzip" ? new GZipStream(output, CompressionMode.Compress, true) : new DeflateStream(output, CompressionMode.Compress, true))
            encoder.Write(body, 0, body.Length);
        return output.ToArray();
    }

    private static void MeasureQValues()
    {
        const string accept = "gzip;q=0.8, br;q=1.0, deflate;q=0.5, *;q=0.1";
        Measure("qvalue-preferred-" + accept.Length, () => new QValueList(true, accept).FindPreferredIndex("br", "gzip", "deflate"), 0);
    }

    private static void MeasureRequestCookies(Assembly assembly)
    {
        var read = StaticDelegate<Func<string, CookieList>>(assembly, "EmbedIO.Net.Internal.RequestCookieFields", "Read");
        const string cookies = "session=abc123; theme=dark; $Version=1; lang=en-US; $Path=/; token=xyz";
        Measure("request-cookies-" + cookies.Length, () => read(cookies).Count, 4);
    }

    private static void MeasureExpectations(Assembly assembly)
    {
        var contains = StaticDelegate<Func<string?, bool>>(assembly, "EmbedIO.Net.Internal.HttpExpectations", "ContainsContinue");
        const string expect = "custom=\"x,y\", 100-continue";
        Measure("expect-continue-" + expect.Length, () => contains(expect) ? 1 : 0, 1);
    }

    private static void MeasureForwarded(Assembly assembly)
    {
        const string parser = "EmbedIO.Forwarding.Internal.ForwardedHeaderParser";
        var parse = StaticDelegate<Func<string, object?>>(assembly, parser, "ParseForwarded");
        var parseLegacy = StaticDelegate<Func<string?, string?, string?, object?>>(assembly, parser, "ParseXForwarded");
        const string forwarded = "for=203.0.113.9;proto=https;host=example.com, for=\"[2001:db8::7]:4711\";proto=https, for=10.0.0.5";
        const string forwardedFor = "203.0.113.9, 2001:db8::7, 10.0.0.5";
        Measure("forwarded-" + forwarded.Length, () => Hops(parse(forwarded)), 3);
        Measure("x-forwarded-for-" + forwardedFor.Length, () => Hops(parseLegacy(forwardedFor, "https, https, http", null)), 3);
    }

    private static int Hops(object? hops)
        => ((ICollection)(hops ?? throw new InvalidOperationException("Rejected forwarding headers."))).Count;

    private static void MeasureQueryFormat()
    {
        var policy = new QueryFormatPolicy("application/json;charset=utf-8", "text/*", "application/x-ndjson");
        const string request = "application/json; charset=\"UTF-8\"";
        Measure("query-format-" + request.Length, () => policy.IsSupported(request) ? 1 : 0, 1);
    }

    private static T StaticDelegate<T>(Assembly assembly, string typeName, string methodName) where T : Delegate
    {
        var type = assembly.GetType(typeName, true) ?? throw new InvalidOperationException("Missing " + typeName + ".");
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing " + methodName + ".");
        return method.CreateDelegate<T>();
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
