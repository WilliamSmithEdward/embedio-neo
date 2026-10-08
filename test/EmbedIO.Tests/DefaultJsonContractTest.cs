using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;
using Json = EmbedIO.Serialization.Json;

namespace EmbedIO.Tests
{
    public class DefaultJsonContractTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ConcreteListPreservesValuesOverRealHttp(bool bufferResponse)
        {
            var first = new Row
            {
                Id = long.MaxValue,
                Name = "caf\u00e9 <>& \"\\\n \ud83d\ude00",
                Amount = 123.4500m,
                At = new DateTime(2026, 10, 7, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567),
            };
            var rows = new List<Row?> { first, null, new Row { Id = 1, Amount = decimal.MaxValue }, first };
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => bufferResponse
                    ? context.SendDataAsync(ResponseSerializer.Json(true), rows)
                    : context.SendDataAsync(rows)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                byte[]? previous = null;
                for (var request = 0; request < 3; request++)
                {
                    using var response = await client.GetAsync(url);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (previous != null) Assert.That(bytes, Is.EqualTo(previous));
                    previous = bytes;
                    using var document = JsonDocument.Parse(bytes);
                    var root = document.RootElement;
                    Assert.That(root.GetArrayLength(), Is.EqualTo(4));
                    Assert.That(root[0].GetProperty("Id").GetInt64(), Is.EqualTo(long.MaxValue));
                    Assert.That(root[0].GetProperty("Name").GetString(), Is.EqualTo(first.Name));
                    Assert.That(root[0].GetProperty("Amount").GetDecimal(), Is.EqualTo(first.Amount));
                    Assert.That(decimal.GetBits(root[0].GetProperty("Amount").GetDecimal()), Is.EqualTo(decimal.GetBits(first.Amount)));
                    Assert.That(root[0].GetProperty("At").GetDateTime(), Is.EqualTo(first.At));
                    Assert.That(root[0].GetProperty("At").GetDateTime().Kind, Is.EqualTo(DateTimeKind.Utc));
                    Assert.That(root[1].ValueKind, Is.EqualTo(JsonValueKind.Null));
                    Assert.That(root[2].GetProperty("Id").GetInt64(), Is.EqualTo(1));
                    Assert.That(root[2].GetProperty("Name").ValueKind, Is.EqualTo(JsonValueKind.Null));
                    Assert.That(root[2].GetProperty("Amount").GetDecimal(), Is.EqualTo(decimal.MaxValue));
                    Assert.That(root[3].GetRawText(), Is.EqualTo(root[0].GetRawText()));
                    Assert.That(root[3].TryGetProperty("$circref", out _), Is.False);
                }
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Test]
        public void DeclaredBaseTypesOmitDerivedMembersUnlessPolymorphismIsConfigured()
        {
            var item = new DerivedRow { Id = 1, Extra = "derived" };
            using var typed = JsonDocument.Parse(Json.Serialize(new List<BaseRow> { item }));
            using var untyped = JsonDocument.Parse(Json.Serialize(new List<object> { item }));
            using var nested = JsonDocument.Parse(Json.Serialize(new { Item = (BaseRow)item }));
            using var dictionary = JsonDocument.Parse(Json.Serialize(new Dictionary<string, BaseRow> { ["item"] = item }));
            Assert.That(typed.RootElement[0].GetProperty("Id").GetInt32(), Is.EqualTo(1));
            Assert.That(typed.RootElement[0].TryGetProperty("Extra", out _), Is.False);
            Assert.That(nested.RootElement.GetProperty("Item").TryGetProperty("Extra", out _), Is.False);
            Assert.That(dictionary.RootElement.GetProperty("item").TryGetProperty("Extra", out _), Is.False);
            Assert.That(untyped.RootElement[0].GetProperty("Extra").GetString(), Is.EqualTo(item.Extra));
        }

        [Test]
        public void PublicFieldsAreIncludedAndDotnetAttributesControlExposure()
        {
            using var document = JsonDocument.Parse(Json.Serialize(new AttributeRow()));
            var root = document.RootElement;
            Assert.That(root.GetProperty("Field").GetInt32(), Is.EqualTo(42));
            Assert.That(root.GetProperty("renamed").GetString(), Is.EqualTo("visible"));
            Assert.That(root.TryGetProperty("Name", out _), Is.False);
            Assert.That(root.TryGetProperty("Secret", out _), Is.False);
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void NonfiniteNumbersUseNamedStringsWhileExplicitStrictOptionsRejectThem(double value)
        {
            using var document = JsonDocument.Parse(Json.Serialize(new { Value = value }));
            var expected = double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";
            Assert.That(document.RootElement.GetProperty("Value").GetString(), Is.EqualTo(expected));
            Assert.Throws<ArgumentException>(() => Json.Serialize(new { Value = value }, new JsonSerializerOptions()));
        }

        [Test]
        public void FiniteDoubleBoundariesRemainJsonNumbers()
        {
            using var document = JsonDocument.Parse(Json.Serialize(new { Max = double.MaxValue, Min = double.Epsilon }));
            Assert.That(document.RootElement.GetProperty("Max").ValueKind, Is.EqualTo(JsonValueKind.Number));
            Assert.That(document.RootElement.GetProperty("Max").GetDouble(), Is.EqualTo(double.MaxValue));
            Assert.That(document.RootElement.GetProperty("Min").GetDouble(), Is.EqualTo(double.Epsilon));
        }

        [Test]
        public void ThrowingGettersAndUnsupportedArraysFailExplicitly()
        {
            Assert.Throws<InvalidOperationException>(() => Json.Serialize(new ThrowingRow()));
            Assert.Throws<NotSupportedException>(() => Json.Serialize(new int[,] { { 1, 2 }, { 3, 4 } }));
        }

        [TestCase("depth")]
        [TestCase("getter")]
        [TestCase("array")]
        [TestCase("cycle")]
        public async Task InvalidPayloadReturnsServerErrorAndNextRequestStillWorks(string failure)
        {
            var cyclic = new CyclicRow();
            cyclic.Next = cyclic;
            var deep = new CyclicRow();
            var current = deep;
            for (var index = 0; index < 70; index++) { current.Next = new CyclicRow(); current = current.Next; }
            object payload = failure switch
            {
                "depth" => deep,
                "getter" => new ThrowingRow(),
                "array" => new int[,] { { 1, 2 } },
                _ => cyclic,
            };
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                    context.SendDataAsync(context.Request.Url.AbsolutePath == "/invalid" ? payload : new Row { Id = 42 })));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var invalid = await client.GetAsync(url + "invalid");
                Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                using var valid = await client.GetAsync(url);
                Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using var document = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
                Assert.That(document.RootElement.GetProperty("Id").GetInt64(), Is.EqualTo(42));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Test]
        public void TimestampOffsetAndBinaryRepresentationsAreExplicit()
        {
            var offset = new DateTimeOffset(2026, 10, 7, 12, 34, 56, TimeSpan.FromHours(2));
            using var document = JsonDocument.Parse(Json.Serialize(new { Offset = offset, Bytes = new byte[] { 0, 1, 255 } }));
            Assert.That(document.RootElement.GetProperty("Offset").GetString(), Is.EqualTo("2026-10-07T12:34:56+02:00"));
            Assert.That(document.RootElement.GetProperty("Offset").GetDateTimeOffset(), Is.EqualTo(offset));
            Assert.That(document.RootElement.GetProperty("Bytes").GetBytesFromBase64(), Is.EqualTo(new byte[] { 0, 1, 255 }));
        }

        public sealed class Row
        {
            public long Id { get; set; }
            public string? Name { get; set; }
            public decimal Amount { get; set; }
            public DateTime At { get; set; }
        }

        public class BaseRow
        {
            public int Id { get; set; }
        }

        public sealed class DerivedRow : BaseRow
        {
            public string? Extra { get; set; }
        }

        public sealed class AttributeRow
        {
            public int Field = 42;
            [JsonPropertyName("renamed")]
            public string Name { get; set; } = "visible";
            [JsonIgnore]
            public string Secret { get; set; } = "hidden";
        }

        public sealed class ThrowingRow
        {
            public int Value => throw new InvalidOperationException("Getter failed.");
        }

        public sealed class CyclicRow
        {
            public CyclicRow? Next { get; set; }
        }
    }
}
