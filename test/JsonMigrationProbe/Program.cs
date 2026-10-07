using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using Json = EmbedIO.Serialization.Json;

namespace JsonMigrationProbe
{
    // Characterizes both parsers; differences are findings, not test failures.
    internal static class Program
    {
        private static readonly JsonSerializerOptions ReportOptions = new JsonSerializerOptions { IncludeFields = true };

        private static async Task Main()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Console.WriteLine($"Runtime: {Environment.Version}; SWAN: {typeof(Swan.Formatters.Json).Assembly.FullName}");
            for (var code = 0; code < 32; code++)
                Compare<TextData>($"raw U+{code:X4}", "{\"Text\":\"first" + (char)code + "second\"}");

            foreach (var (name, body) in new[] {
                ("raw CRLF", "{\"Text\":\"first\r\nsecond\"}"),
                ("escaped CRLF", "{\"Text\":\"first\\r\\nsecond\"}"),
                ("outside CRLF", "{\r\n\"Text\":\"first second\"\r\n}"),
                ("trailing comma", "{\"Text\":\"a\",}"),
                ("line comment", "{\"Text\":\"a\" // note\n}"),
                ("block comment", "{\"Text\":/* note */\"a\"}"),
                ("single quotes", "{'Text':'a'}"),
                ("unquoted key", "{Text:\"a\"}"),
                ("duplicate property", "{\"Text\":\"a\",\"Text\":\"b\"}"),
                ("lowercase property", "{\"text\":\"a\"}"),
                ("case duplicate", "{\"Text\":\"a\",\"text\":\"b\"}"),
                ("null string", "{\"Text\":null}"),
                ("numeric string target", "{\"Text\":12.5}"),
                ("boolean string target", "{\"Text\":true}"),
                ("object string target", "{\"Text\":{\"x\":1}}"),
                ("unknown escape", "{\"Text\":\"a\\qb\"}"),
                ("trailing garbage", "{\"Text\":\"a\"}garbage"),
                ("empty body", ""),
            }) Compare<TextData>(name, body);

            foreach (var (name, body) in new[] {
                ("integer", "{\"Value\":1}"),
                ("fraction", "{\"Value\":1.25}"),
                ("exponent", "{\"Value\":1e3}"),
                ("huge integer", "{\"Value\":79228162514264337593543950336}"),
                ("huge exponent", "{\"Value\":1e100}"),
                ("tiny exponent", "{\"Value\":1e-100}"),
                ("leading zero", "{\"Value\":01}"),
                ("leading plus", "{\"Value\":+1}"),
                ("array trailing comma", "{\"Value\":[1,2,]}"),
                ("duplicate untyped", "{\"Value\":1,\"Value\":2}"),
                ("nested raw LF", "{\"Value\":[{\"x\":\"a\nb\"}]}"),
            }) Compare<object>(name, body);

            Compare<NumberData>("quoted integer", "{\"Value\":\"12\"}");
            Compare<NumberData>("null integer", "{\"Value\":null}");
            Compare<NumberData>("fraction to integer", "{\"Value\":1.9}");
            Compare<NumberData>("overflow integer", "{\"Value\":2147483648}");
            Compare<NumberData>("invalid quoted integer", "{\"Value\":\"oops\"}");
            Compare<EnumData>("enum name", "{\"Value\":\"Second\"}");
            Compare<EnumData>("enum number", "{\"Value\":1}");
            Compare<DateData>("ISO date", "{\"Value\":\"2026-10-07T12:30:00Z\"}");
            Compare<DateData>("non ISO date", "{\"Value\":\"10/07/2026\"}");
            Compare<object>("scalar string", "\"hello\"");
            Compare<object>("scalar number", "123");
            Compare<object>("scalar null", "null");
            Compare<object>("array root", "[1,2]");
            Compare<object>("escaped surrogate pair", "{\"Value\":\"\\uD83D\\uDE00\"}");
            Compare<object>("unpaired escaped surrogate", "{\"Value\":\"\\uD800\"}");
            Compare<BoolData>("quoted boolean", "{\"Value\":\"true\"}");
            Compare<BoolData>("numeric boolean", "{\"Value\":1}");
            Compare<MemberData>("fields setters and read-only members", "{\"Field\":3,\"Count\":4,\"PrivateCount\":5,\"ReadOnly\":6,\"Numbers\":[1,2]}");
            Compare<ConstructorData>("parameterized constructor", "{\"Value\":4}");
            Compare<Dictionary<string, int>>("dictionary values", "{\"one\":\"1\",\"two\":2}");
            Compare<int[]>("typed array", "[1,\"2\",3]");
            Compare<DateData>("invalid date", "{\"Value\":\"invalid\"}");
            Compare<object>("quoted date untyped", "{\"Value\":\"2026-10-07T12:30:00Z\"}");

            foreach (var (name, value) in new (string, object?)[] {
                ("enum output", new EnumData { Value = Choice.Second }),
                ("UTC date output", new DateData { Value = new DateTime(2026, 10, 7, 12, 30, 0, DateTimeKind.Utc) }),
                ("null property output", new TextData()),
                ("Unicode and HTML output", new TextData { Text = "é <tag> \" \\" }),
                ("byte array output", new { Value = new byte[] { 0, 1, 255 } }),
                ("integer dictionary keys", new Dictionary<int, string> { [1] = "a" }),
                ("NaN output", new { Value = double.NaN }),
                ("infinity output", new { Value = double.PositiveInfinity }),
            })
            {
                foreach (var parser in new[] { "SWAN 3.1.0", "Neo defaults" })
                {
                    try
                    {
                        var output = parser == "SWAN 3.1.0" ? Swan.Formatters.Json.Serialize(value) : Json.Serialize(value);
                        Console.WriteLine(JsonSerializer.Serialize(new { name, parser, accepted = true, output }));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { name, parser, accepted = false, error = ex.GetType().Name }));
                    }
                }
            }

            VerifyDefaults();

            foreach (var (name, body) in new[] {
                ("raw CRLF", "{\"Text\":\"first\r\nsecond\"}"),
                ("escaped CRLF", "{\"Text\":\"first\\r\\nsecond\"}"),
            })
            {
                using var server = new TestWebServer();
                server.WithWebApi("/api", module => module.WithController<EchoController>());
                server.OnAny(async context => await context.SendDataAsync(await context.GetRequestDataAsync<TextData>()));
                server.Start();
                foreach (var path in new[] { "/api/echo", "/callback" })
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var response = await server.Client.PostAsync(path, content);
                    const int expectedStatus = 200;
                    if ((int)response.StatusCode != expectedStatus)
                        throw new InvalidOperationException($"Unexpected HTTP status for {name} at {path}.");
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (expectedStatus == 200 && Json.Deserialize<TextData>(responseBody).Text != "first\r\nsecond")
                        throw new InvalidOperationException($"CRLF value changed at {path}.");
                    Console.WriteLine(JsonSerializer.Serialize(new { name, path, status = (int)response.StatusCode, body = responseBody }));
                }
            }
        }

        private static void VerifyDefaults()
        {
            var assertions = 0;
            var value = Json.Deserialize<CompatibilityData>("{\"Text\":\"first\r\nsecond\",\"Flag\":\"true\",\"Choice\":\"Second\",\"Count\":+001,\"PrivateCount\":7,\"Date\":\"10/07/2026\",}");
            Check(value.Text == "first\r\nsecond", "raw CRLF");
            Check(value.Flag, "quoted boolean");
            Check(value.Choice == Choice.Second, "enum name");
            Check(value.Count == 1, "legacy number");
            Check(value.PrivateCount == 7, "private setter");
            Check(value.Date == new DateTime(2026, 10, 7), "legacy date");
            Check(Json.Deserialize<TextData>(" \r\n") == null, "empty reference");
            Check(Json.Deserialize<int>("") == 0, "empty value");
            var members = Json.Deserialize<MemberData>("{\"Field\":3,\"PrivateCount\":5,\"ReadOnly\":6}");
            Check(members.Field == 3 && members.PrivateCount == 5 && members.ReadOnly == 9, "member binding");
            var options = Json.CreateOptions();
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            Check(Json.Serialize(new EnumData { Value = Choice.Second }, options).Contains("Second"), "custom converter precedence");
            Check(double.IsNaN(Json.Deserialize<double[]>(Json.Serialize(new[] { double.NaN }))[0]), "named float");
            var nested = (Dictionary<string, object>)Json.Deserialize("{\"a\nb\":{\"Text\":\"x\ty\"}}")!;
            Check(((Dictionary<string, object>)nested["a\nb"])["Text"].Equals("x\ty"), "nested controls");
            Reject("{\"Text\":\"a\nb\"}", new JsonSerializerOptions());
            Reject("{\"Count\":\"invalid\"}");
            Reject("{\"Text\":\"a\\qb\"}");
            Reject("{}garbage");
            Console.WriteLine($"Verification assertions: {assertions}");

            void Check(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException("Compatibility verification failed: " + name);
                assertions++;
            }
            void Reject(string body, JsonSerializerOptions? settings = null)
            {
                try { Json.Deserialize<CompatibilityData>(body, settings); }
                catch (JsonException) { assertions++; return; }
                throw new InvalidOperationException("Invalid JSON was accepted: " + body);
            }
        }

        private static void Compare<T>(string name, string body)
        {
            foreach (var parser in new[] { "SWAN 3.1.0", "Neo defaults", "Explicit strict options" })
            {
                try
                {
                    object? value = parser == "SWAN 3.1.0" ? Swan.Formatters.Json.Deserialize<T>(body)
                        : Json.Deserialize<T>(body, parser == "Explicit strict options" ? new JsonSerializerOptions() : null);
                    Console.WriteLine(JsonSerializer.Serialize(new { name, parser, accepted = true, type = value?.GetType().FullName, value, stringUnits = StringUnits(value) }, ReportOptions));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { name, parser, accepted = false, error = ex.GetType().Name }));
                }
            }
        }

        private static object? StringUnits(object? value)
        {
            if (value is string text) return text.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)).ToArray();
            if (value is TextData data) return StringUnits(data.Text);
            if (value is IDictionary<string, object> dictionary)
                return dictionary.ToDictionary(pair => pair.Key, pair => StringUnits(pair.Value));
            if (value is IList<object> list) return list.Select(StringUnits).ToArray();
            return null;
        }


    }

    public sealed class CompatibilityData
    {
        public string? Text { get; set; }
        public bool Flag { get; set; }
        public Choice Choice { get; set; }
        public int Count { get; set; }
        public int PrivateCount { get; private set; }
        public DateTime Date { get; set; }
    }
    public sealed class MemberData
    {
        public int Field;
        public int Count { get; set; } = 7;
        public int PrivateCount { get; private set; } = 8;
        public int ReadOnly => 9;
        public List<int> Numbers { get; } = new List<int> { 9 };
    }
    public sealed class ConstructorData
    {
        public ConstructorData(int value) { Value = value; }
        public int Value { get; }
    }
    public sealed class TextData { public string? Text { get; set; } }
    public sealed class BoolData { public bool Value { get; set; } }
    public sealed class NumberData { public int Value { get; set; } }
    public enum Choice { First, Second }
    public sealed class EnumData { public Choice Value { get; set; } }
    public sealed class DateData { public DateTime Value { get; set; } }
    public sealed class EchoController : WebApiController
    {
        [Route(HttpVerbs.Post, "/echo")]
        public TextData Echo([JsonData] TextData data) => data;
    }
}
