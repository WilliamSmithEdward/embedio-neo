using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;
using Json = EmbedIO.Serialization.Json;

namespace EmbedIO.Tests.Issues
{
    public class Issue153_JsonCompatibility
    {
        private static IEnumerable<int> Controls()
        {
            for (var value = 0; value < 32; value++) yield return value;
        }

        [TestCaseSource(nameof(Controls))]
        public void RawStringControlsPreserveTheirValuesAndKeys(int code)
        {
            var text = "first" + (char)code + "second";
            var body = "{\"" + text + "\":\"" + text + "\"}";
            var data = Json.Deserialize<Dictionary<string, string>>(body);
            Assert.That(data[text], Is.EqualTo(text));
            var untyped = (Dictionary<string, object>)Json.Deserialize(body)!;
            Assert.That(untyped[text], Is.EqualTo(text));
            using var serialized = JsonDocument.Parse(Json.Serialize(data));
            Assert.That(serialized.RootElement.GetProperty(text).GetString(), Is.EqualTo(text));
        }

        [TestCase("+001", 1)]
        [TestCase("-001", -1)]
        [TestCase(".5", 0.5)]
        [TestCase("-.5", -0.5)]
        [TestCase("1.", 1)]
        [TestCase("001.25", 1.25)]
        [TestCase("+001.25e2", 125)]
        public void LegacyNumbersAndTrailingCommasKeepTheirValues(string number, decimal expected)
        {
            var body = "{\"Values\":[" + number + ",],\"Text\":\"+001 .5 1.\",}";
            var model = Json.Deserialize<NumberList>(body);
            Assert.That(model.Values, Is.EqualTo(new[] { expected }));
            Assert.That(model.Text, Is.EqualTo("+001 .5 1."));
        }

        [TestCase("{\"Text\":\"a\\qb\"}")]
        [TestCase("{\"Text\":\"a\\\nb\"}")]
        [TestCase("{\"Text\":\"a\"}garbage")]
        [TestCase("{\"Text\":\"a\nb\",\"Bad\":}")]
        [TestCase("{\"Text\":\"a\nb\"")]
        [TestCase("{\"Value\":.}")]
        [TestCase("{\"Value\":+.}")]
        [TestCase("{\"Value\":.e1}")]
        [TestCase("{\"Value\":+}")]
        [TestCase("{\"Value\":00x}")]
        [TestCase("{\"Value\":1+2}")]
        [TestCase("{\"Value\":NaN}")]
        [TestCase("{\"Value\":01.2.3}")]
        [TestCase("{\"Value\":1e100}")]
        [TestCase("{\"Value\":\"\\uD800\"}")]
        [TestCase("{\"Value\":/* comment */ 1}")]
        [TestCase("{\"Value\":1}\0")]
        public void UnrelatedMalformedOrLossyInputStillFails(string body)
            => Assert.Throws<JsonException>(() => Json.Deserialize(body));

        [Test]
        public void QuotesBackslashesExistingEscapesAndWhitespaceRemainIntact()
        {
            var text = "quote \" slash \\ pair \\\" actual\r\n escaped \\r\\n emoji 😀";
            var encoded = JsonSerializer.Serialize(text);
            var body = "{\r\n\"Text\":" + encoded.Replace("actual\\r\\n", "actual\r\n") + "\r\n}";
            Assert.That(Json.Deserialize<TextData>(body).Text, Is.EqualTo(text));
        }

        [TestCase("")]
        [TestCase(" \r\n\t")]
        public void EmptyInputMatchesLegacyDefaults(string body)
        {
            Assert.That(Json.Deserialize<TextData>(body), Is.Null);
            Assert.That(Json.Deserialize(body, typeof(TextData)), Is.Null);
            Assert.That(Json.Deserialize<int>(body), Is.Zero);
            Assert.That(Json.Deserialize(body, typeof(int)), Is.EqualTo(0));
            Assert.That(Json.Deserialize<int?>(body), Is.Null);
        }

        [Test]
        public void CopiedEmbedIOOptionsRetainCompatibilityAndCustomOverrides()
        {
            var options = new JsonSerializerOptions(Json.CreateOptions());
            options.Converters.Add(new JsonStringEnumConverter());
            options.Converters.Add(new YesBooleanConverter());
            var data = Json.Deserialize<Values>("{\"Flag\":\"yes\",\"Choice\":\"Second\",}", options);
            Assert.That(data.Flag, Is.True);
            Assert.That(data.Choice, Is.EqualTo(Choice.Second));
            Assert.That(Json.Serialize(data, options), Does.Contain("\"Second\""));
            Assert.That(Json.Deserialize<TextData>("{\"Text\":\"a\nb\"}", options).Text, Is.EqualTo("a\nb"));
        }

        [TestCase("{\"Text\":\"a\nb\"}")]
        [TestCase("{\"Text\":\"a\",}")]
        [TestCase("{\"Text\":\"a\",\"Value\":+01}")]
        [TestCase("")]
        public void ExplicitStrictOptionsStillRejectLegacySyntax(string body)
        {
            var options = new JsonSerializerOptions();
            Assert.Throws<JsonException>(() => Json.Deserialize<TextData>(body, options));
            Assert.Throws<JsonException>(() => Json.Deserialize(body, typeof(TextData), options));
        }

        [Test]
        public void ExplicitCommentHandlingDoesNotConfuseStringOrNumberNormalization()
        {
            var options = Json.CreateOptions();
            options.ReadCommentHandling = JsonCommentHandling.Skip;
            var body = "{/* \" backslash \\ */\"Values\":[+01,],// \" quoted comment\n\"Text\":\"a\nb\"}";
            var model = Json.Deserialize<NumberList>(body, options);
            Assert.That(model.Values, Is.EqualTo(new[] { 1m }));
            Assert.That(model.Text, Is.EqualTo("a\nb"));
            Assert.Throws<JsonException>(() => Json.Deserialize<object>("{/* unterminated", options));
        }

        [TestCase("\"true\"", true)]
        [TestCase("\"FALSE\"", false)]
        [TestCase("true", true)]
        [TestCase("false", false)]
        public void BooleanStringsAndTokensBindWithoutChangingOutput(string value, bool expected)
        {
            var data = Json.Deserialize<Values>("{\"Flag\":" + value + "}");
            Assert.That(data.Flag, Is.EqualTo(expected));
            Assert.That(Json.Deserialize<Values>(Json.Serialize(data)).Flag, Is.EqualTo(expected));
        }

        [TestCase("\"Second\"")]
        [TestCase("\"1\"")]
        [TestCase("1")]
        public void EnumNamesAndNumbersBindWithNumericOutput(string value)
        {
            var data = Json.Deserialize<Values>("{\"Choice\":" + value + ",\"MaybeChoice\":" + value + "}");
            Assert.That(data.Choice, Is.EqualTo(Choice.Second));
            Assert.That(data.MaybeChoice, Is.EqualTo(Choice.Second));
            using var document = JsonDocument.Parse(Json.Serialize(data));
            Assert.That(document.RootElement.GetProperty("Choice").GetInt32(), Is.EqualTo(1));
        }

        [TestCase("{\"Flag\":\"invalid\"}")]
        [TestCase("{\"Flag\":1}")]
        [TestCase("{\"Choice\":\"invalid\"}")]
        [TestCase("{\"Choice\":1.5}")]
        [TestCase("{\"Choice\":4294967296}")]
        [TestCase("{\"Count\":\"invalid\"}")]
        [TestCase("{\"Count\":null}")]
        [TestCase("{\"Count\":2147483648}")]
        [TestCase("{\"Count\":1.5}")]
        public void FailedValueConversionsAreNeverSilentlyDefaulted(string body)
            => Assert.Throws<JsonException>(() => Json.Deserialize<Values>(body));

        [Test]
        public void ValidNonIsoDatesBindButUtcAndOffsetsRemainExplicit()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                var legacy = Json.Deserialize<DateValues>("{\"Date\":\"10/07/2026\",\"Offset\":\"10/07/2026 12:30:00 +02:00\"}");
                Assert.That(legacy.Date, Is.EqualTo(new DateTime(2026, 10, 7)));
                Assert.That(legacy.Offset.Offset, Is.EqualTo(TimeSpan.FromHours(2)));
                var iso = Json.Deserialize<DateValues>("{\"Date\":\"2026-10-07T12:30:00Z\",\"Offset\":\"2026-10-07T12:30:00+02:00\"}");
                Assert.That(iso.Date.Kind, Is.EqualTo(DateTimeKind.Utc));
                Assert.That(Json.Serialize(iso), Does.Contain("12:30:00Z"));
                Assert.That(Json.Deserialize<DateValues>(Json.Serialize(iso)).Offset, Is.EqualTo(iso.Offset));
                Assert.Throws<JsonException>(() => Json.Deserialize<DateValues>("{\"Date\":\"invalid\"}"));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Test]
        public void LegacyPrivateSettersBindWithoutOverridingIgnoredOrReadOnlyMembers()
        {
            var data = Json.Deserialize<MemberData>("{\"Count\":4,\"Ignored\":5,\"ReadOnly\":6,\"IgnoreReads\":10,\"IgnoreWrites\":11}");
            Assert.That(data.Count, Is.EqualTo(4));
            Assert.That(data.Ignored, Is.EqualTo(7));
            Assert.That(data.ReadOnly, Is.EqualTo(8));
            Assert.That(data.IgnoreReads, Is.EqualTo(9));
            Assert.That(data.IgnoreWrites, Is.EqualTo(11));
            using var document = JsonDocument.Parse(Json.Serialize(data));
            Assert.That(document.RootElement.GetProperty("IgnoreReads").GetInt32(), Is.EqualTo(9));
            Assert.That(document.RootElement.TryGetProperty("IgnoreWrites", out _), Is.False);
            var strict = Json.Deserialize<MemberData>("{\"Count\":4}", new JsonSerializerOptions());
            Assert.That(strict.Count, Is.EqualTo(3));
        }

        [Test]
        public void NonFiniteFloatingPointValuesUseLegacyNamedStrings()
        {
            var body = Json.Serialize(new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity });
            Assert.That(body, Is.EqualTo("[\"NaN\",\"Infinity\",\"-Infinity\"]"));
            var values = Json.Deserialize<double[]>(body);
            Assert.That(double.IsNaN(values[0]), Is.True);
            Assert.That(values[1], Is.EqualTo(double.PositiveInfinity));
            Assert.That(values[2], Is.EqualTo(double.NegativeInfinity));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DefaultRequestBindingTransparentlyAcceptsLegacyPayloads(bool controller)
        {
            using var server = new TestWebServer();
            server.WithWebApi("/api", module => module.WithController<EchoController>());
            server.OnAny(async context => await context.SendDataAsync(await context.GetRequestDataAsync<Values>()));
            server.Start();
            var body = "{\"Text\":\"first\r\nsecond\",\"Flag\":\"true\",\"Choice\":\"Second\",\"Count\":+001,}";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await server.Client.PostAsync(controller ? "/api/echo" : "/callback", content);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var data = Json.Deserialize<Values>(await response.Content.ReadAsStringAsync());
            Assert.That(data.Text, Is.EqualTo("first\r\nsecond"));
            Assert.That(data.Flag, Is.True);
            Assert.That(data.Choice, Is.EqualTo(Choice.Second));
            Assert.That(data.Count, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DefaultRequestBindingStillReturns400ForInvalidConversions(bool controller)
        {
            using var server = new TestWebServer();
            server.WithWebApi("/api", module => module.WithController<EchoController>());
            server.OnAny(async context => await context.SendDataAsync(await context.GetRequestDataAsync<Values>()));
            server.Start();
            using var content = new StringContent("{\"Text\":\"a\nb\",\"Count\":\"invalid\"}", Encoding.UTF8, "application/json");
            using var response = await server.Client.PostAsync(controller ? "/api/echo" : "/callback", content);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task DeserializerSnapshotKeepsCompatibilityAndTheChosenStrictPolicy()
        {
            var options = Json.CreateOptions();
            var compatible = RequestDeserializer.Json<TextData>(options);
            var strict = RequestDeserializer.Json<TextData>(new JsonSerializerOptions());
            options.Converters.Clear();
            using var server = new TestWebServer();
            server.OnAny(async context => await context.SendDataAsync(await context.GetRequestDataAsync(context.Request.Url.AbsolutePath == "/strict" ? strict : compatible)));
            server.Start();
            foreach (var path in new[] { "/compatible", "/strict" })
            {
                using var content = new StringContent("{\"Text\":\"a\nb\"}", Encoding.UTF8, "application/json");
                using var response = await server.Client.PostAsync(path, content);
                Assert.That(response.StatusCode, Is.EqualTo(path == "/strict" ? HttpStatusCode.BadRequest : HttpStatusCode.OK));
            }
        }

        public sealed class TextData { public string? Text { get; set; } }
        public sealed class NumberList { public decimal[] Values { get; set; } = Array.Empty<decimal>(); public string? Text { get; set; } }
        public enum Choice { First, Second }
        public sealed class Values
        {
            public string? Text { get; set; }
            public bool Flag { get; set; }
            public Choice Choice { get; set; }
            public Choice? MaybeChoice { get; set; }
            public int Count { get; set; }
        }
        public sealed class MemberData
        {
            public int Count { get; private set; } = 3;
            [JsonIgnore] public int Ignored { get; private set; } = 7;
            public int ReadOnly => 8;
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)] public int IgnoreReads { get; private set; } = 9;
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public int IgnoreWrites { get; private set; }
        }
        public sealed class DateValues { public DateTime Date { get; set; } public DateTimeOffset Offset { get; set; } }
        public sealed class EchoController : WebApiController
        {
            [Route(HttpVerbs.Post, "/echo")]
            public Values Echo([JsonData] Values data) => data;
        }
        private sealed class YesBooleanConverter : JsonConverter<bool>
        {
            public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() == "yes";
            public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteStringValue(value ? "yes" : "no");
        }
    }
}
