using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue163_TypedRouteValidation
    {
        private static readonly string[] InvalidPaths =
        {
            "u/not-number", "u/65536", "u/-1", "u/%20", "i/2147483648", "l/9223372036854775808",
            "money/not-money", "number/not-number", "flag/maybe", "guid/not-a-guid",
            "date/not-a-date", "time/not-a-time", "letter/two", "choice/unknown", "optional/oops",
            "custom/bad-format", "custom/bad-overflow", "custom/bad-argument",
        };

        public static IEnumerable<object[]> InvalidRoutes()
        {
            foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
                foreach (var path in InvalidPaths) yield return new object[] { mode, path };
        }

        [TestCaseSource(nameof(InvalidRoutes))]
        public async Task MalformedSupportedRouteValuesReturn400BeforeMethodInvocation(HttpListenerMode mode, string path)
        {
            await using var fixture = new Fixture(mode);
            using var response = await fixture.Client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), path);
            Assert.That(fixture.State.Calls, Is.Zero);
            Assert.That(fixture.State.Created, Is.EqualTo(1));
            Assert.That(fixture.State.Disposed, Is.EqualTo(1));
            using var healthy = await fixture.Client.GetAsync("u/7");
            Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fixture.State.Last, Is.EqualTo("7"));
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task OriginalUshortInputsKeepBinarySerializerAndRecoveryHealthy(HttpListenerMode mode, bool buffered)
        {
            await using var fixture = new Fixture(mode, raw: true, buffered: buffered);
            foreach (var value in new[] { "not-number", "65536" })
            {
                using var response = await fixture.Client.GetAsync("raw/" + value);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(fixture.State.Calls, Is.Zero);
            }
            Assert.That(await fixture.Client.GetByteArrayAsync("raw/7"), Is.EqualTo(new byte[] { 42 }));
            Assert.That(fixture.State.Last, Is.EqualTo("7"));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task SuccessfulConversionsDefaultsNullableAndMissingRoutesKeepTheirBehavior(HttpListenerMode mode)
        {
            await using var fixture = new Fixture(mode);
            foreach (var pair in new[] { ("u/0", "0"), ("u/65535", "65535"), ("u/0xFFFF", "65535"), ("i/-7", "-7"), ("l/9223372036854775807", "9223372036854775807"),
                ("money/12.5", "12.5"), ("number/1.5", "1.5"), ("number/NaN", "NaN"), ("number/Infinity", "Infinity"), ("flag/true", "True"), ("letter/Z", "Z"),
                ("guid/00112233-4455-6677-8899-aabbccddeeff", "00112233-4455-6677-8899-aabbccddeeff"),
                ("date/2026-10-07", "2026-10-07"), ("time/00:01:02", "00:01:02"), ("choice/oNe", "One"), ("choice/99", "99"),
                ("optional", "none"), ("optional/9", "9"), ("text", "empty"), ("defaults", "42"), ("custom/ok", "ok") })
            {
                using var response = await fixture.Client.GetAsync(pair.Item1);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), pair.Item1);
                Assert.That(fixture.State.Last, Is.EqualTo(pair.Item2), pair.Item1);
            }
            foreach (var path in new[] { "u", "u/", "not-a-route" })
            {
                using var response = await fixture.Client.GetAsync(path);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), path);
            }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        [SetCulture("fr-FR")]
        public async Task RouteNumbersKeepInvariantConversionDespiteHostCulture(HttpListenerMode mode)
        {
            await using var fixture = new Fixture(mode);
            using var valid = await fixture.Client.GetAsync("money/12.5");
            Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fixture.State.Last, Is.EqualTo("12.5"));
            using var invalid = await fixture.Client.GetAsync("money/bogus");
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [TestCase(HttpListenerMode.EmbedIO, "format")]
        [TestCase(HttpListenerMode.Microsoft, "format")]
        [TestCase(HttpListenerMode.EmbedIO, "overflow")]
        [TestCase(HttpListenerMode.Microsoft, "overflow")]
        [TestCase(HttpListenerMode.EmbedIO, "argument")]
        [TestCase(HttpListenerMode.Microsoft, "argument")]
        [TestCase(HttpListenerMode.EmbedIO, "unsupported")]
        [TestCase(HttpListenerMode.Microsoft, "unsupported")]
        [TestCase(HttpListenerMode.EmbedIO, "internal")]
        [TestCase(HttpListenerMode.Microsoft, "internal")]
        public async Task ControllerExceptionsRemain500AfterAValidConversion(HttpListenerMode mode, string error)
        {
            await using var fixture = new Fixture(mode);
            using var response = await fixture.Client.GetAsync("throws/7?error=" + error);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(fixture.State.Calls, Is.EqualTo(1));
            Assert.That(fixture.State.Disposed, Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO, "unsupported/value")]
        [TestCase(HttpListenerMode.Microsoft, "unsupported/value")]
        [TestCase(HttpListenerMode.EmbedIO, "custom/not-supported")]
        [TestCase(HttpListenerMode.Microsoft, "custom/not-supported")]
        [TestCase(HttpListenerMode.EmbedIO, "custom/internal")]
        [TestCase(HttpListenerMode.Microsoft, "custom/internal")]
        [TestCase(HttpListenerMode.EmbedIO, "custom/generic-wrapper")]
        [TestCase(HttpListenerMode.Microsoft, "custom/generic-wrapper")]
        [TestCase(HttpListenerMode.EmbedIO, "custom/wrong-type")]
        [TestCase(HttpListenerMode.Microsoft, "custom/wrong-type")]
        [TestCase(HttpListenerMode.EmbedIO, "required-optional")]
        [TestCase(HttpListenerMode.Microsoft, "required-optional")]
        public async Task UnsupportedConversionAndOptionalRouteMisconfigurationRemain500(HttpListenerMode mode, string path)
        {
            await using var fixture = new Fixture(mode);
            using var response = await fixture.Client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError), path);
            Assert.That(fixture.State.Calls, Is.Zero);
            Assert.That(fixture.State.Disposed, Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task RequestAwareReleaseIsAwaitedOnInvalidRouteBinding(HttpListenerMode mode)
        {
            await using var fixture = new Fixture(mode, scoped: true);
            using var response = await fixture.Client.GetAsync("u/oops");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(fixture.State.Calls, Is.Zero);
            Assert.That(fixture.State.Disposed, Is.EqualTo(1));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task QueryBodyAndExplicitBindingPriorityKeepTheirExistingStatus(HttpListenerMode mode)
        {
            await using var fixture = new Fixture(mode);
            using var query = await fixture.Client.GetAsync("query?value=oops");
            Assert.That(query.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            using var precedence = await fixture.Client.GetAsync("query-priority/not-a-number?value=8");
            Assert.That(precedence.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fixture.State.Last, Is.EqualTo("8"));
            using var invalid = await fixture.Client.PostAsync("body", new StringContent("{broken", Encoding.UTF8, "application/json"));
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            using var valid = await fixture.Client.PostAsync("body", new StringContent("{\"Value\":9}", Encoding.UTF8, "application/json"));
            Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fixture.State.Last, Is.EqualTo("9"));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task EarlyBindingRejectionAnswersAPartialPostAndServesAFreshRequest(HttpListenerMode mode)
        {
            await using var fixture = new Fixture(mode);
            using var socket = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var uri = fixture.Client.BaseAddress;
            await socket.ConnectAsync((uri ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Host, uri.Port, timeout.Token);
            var stream = socket.GetStream();
            // A single small packet contains headers and one body byte. Stop sending
            // when the early rejection closes the connection, as declared by its header.
            // Continuing a bulk upload after a forced close can reset the transport;
            // both inherited 400 and 500 policies force closure in managed mode.
            var request = "POST /api/u/oops HTTP/1.1\r\nHost: " + uri.Authority
                + "\r\nContent-Length: 65536\r\nConnection: keep-alive\r\n\r\nb";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            // Native HttpListener may keep its response connection alive. Read the
            // complete header block rather than incorrectly waiting for native EOF.
            var headers = new StringBuilder();
            for (var lineCount = 0; ; lineCount++)
            {
                Assert.That(lineCount, Is.LessThan(32), "Bound the response header probe.");
                var line = await reader.ReadLineAsync(timeout.Token);
                Assert.That(line, Is.Not.Null, "Response ended before the header block.");
                if (line.Length == 0) break;
                headers.AppendLine(line);
            }
            Assert.That(headers.ToString(), Does.StartWith("HTTP/1.1 400"));
            if (mode == HttpListenerMode.EmbedIO)
                Assert.That(headers.ToString(), Does.Contain("Connection: close").IgnoreCase);
            Assert.That(fixture.State.Calls, Is.Zero);
            using var valid = await fixture.Client.GetAsync("u/7");
            Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fixture.State.Last, Is.EqualTo("7"));
        }

        public enum Choice { One = 1 }
        public sealed class Input { public int Value { get; set; } }
        public sealed class Unsupported { }
        [TypeConverter(typeof(ValidatedConverter))]
        public sealed class Validated { public string Value { get; set; } = string.Empty; }
        public sealed class ValidatedConverter : TypeConverter
        {
            public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);
            public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
                => (string)value switch
                {
                    "bad-format" => throw new FormatException("Invalid format."),
                    "bad-overflow" => throw new OverflowException("Out of range."),
                    "bad-argument" => throw new ArgumentException("Invalid argument."),
                    "not-supported" => throw new NotSupportedException("Converter not implemented."),
                    "internal" => throw new InvalidOperationException("Application converter bug."),
                    "wrong-type" => new object(),
                    "generic-wrapper" => throw new Exception("Application wrapper.", new FormatException("Application failure.")),
                    _ => new Validated { Value = (string)value },
                };
        }

        public sealed class State
        {
            public int Created;
            public int Calls;
            public int Disposed;
            public string? Last;
        }

        public sealed class Controller : WebApiController, IDisposable
        {
            private readonly State _state;
            public Controller(State state) { _state = state; Interlocked.Increment(ref _state.Created); }
            public void Dispose() => Interlocked.Increment(ref _state.Disposed);
            private string Record(object? value) { Interlocked.Increment(ref _state.Calls); return _state.Last = value?.ToString() ?? "none"; }
            [Route(HttpVerbs.Any, "/u/{value}")] public string U(ushort value) => Record(value);
            [Route(HttpVerbs.Get, "/i/{value}")] public string I(int value) => Record(value);
            [Route(HttpVerbs.Get, "/l/{value}")] public string L(long value) => Record(value);
            [Route(HttpVerbs.Get, "/money/{value}")] public string Money(decimal value) => Record(value.ToString(CultureInfo.InvariantCulture));
            [Route(HttpVerbs.Get, "/number/{value}")] public string Number(double value) => Record(value.ToString(CultureInfo.InvariantCulture));
            [Route(HttpVerbs.Get, "/flag/{value}")] public string Flag(bool value) => Record(value);
            [Route(HttpVerbs.Get, "/guid/{value}")] public string GuidValue(Guid value) => Record(value);
            [Route(HttpVerbs.Get, "/date/{value}")] public string Date(DateTime value) => Record(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            [Route(HttpVerbs.Get, "/time/{value}")] public string Time(TimeSpan value) => Record(value);
            [Route(HttpVerbs.Get, "/letter/{value}")] public string Letter(char value) => Record(value);
            [Route(HttpVerbs.Get, "/choice/{value}")] public string EnumValue(Choice value) => Record(value);
            [Route(HttpVerbs.Get, "/optional/{value?}")] public string Optional(ushort? value) => Record(value);
            [Route(HttpVerbs.Get, "/text/{value?}")]
            public string Text(string value)
            {
                if (value is null) throw new System.NullReferenceException();
                return Record(value.Length == 0 ? "empty" : value);
            }
            [Route(HttpVerbs.Get, "/defaults")] public string Defaults(int value = 42) => Record(value);
            [Route(HttpVerbs.Get, "/required-optional/{value?}")] public string RequiredOptional(ushort value) => Record(value);
            [Route(HttpVerbs.Get, "/unsupported/{value}")] public string NotSupported(Unsupported value) => Record(value);
            [Route(HttpVerbs.Get, "/custom/{value}")]
            public string Custom(Validated value)
            {
                if (value is null) throw new System.NullReferenceException();
                return Record(value.Value);
            }
            [Route(HttpVerbs.Get, "/query")] public string Query([QueryField] ushort value) => Record(value);
            [Route(HttpVerbs.Get, "/query-priority/{value}")] public string QueryPriority([QueryField] ushort value) => Record(value);
            [Route(HttpVerbs.Post, "/body")]
            public string Body([JsonData] Input data)
            {
                if (data is null) throw new System.NullReferenceException();
                return Record(data.Value);
            }
            [Route(HttpVerbs.Get, "/raw/{value}")]
            public byte[] Raw(ushort value) { Record(value); Response.ContentType = "application/octet-stream"; Response.ContentEncoding = null; return new byte[] { 42 }; }
            [Route(HttpVerbs.Get, "/throws/{value}")]
            public string Throws(ushort value, [QueryField] string error)
            {
                Record(value);
                throw error switch
                {
                    "format" => new FormatException("Application format error."),
                    "overflow" => new OverflowException("Application overflow."),
                    "argument" => new ArgumentException("Application argument error."),
                    "unsupported" => new NotSupportedException("Application unsupported operation."),
                    _ => new InvalidOperationException("Application internal error."),
                };
            }
        }

        private sealed class Fixture : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new();
            private readonly WebServer _server;
            private readonly Task _running;
            public State State { get; } = new();
            public HttpClient Client { get; }
            public Fixture(HttpListenerMode mode, bool raw = false, bool buffered = false, bool scoped = false)
            {
                var url = TestObjects.Resources.GetServerAddress();
                var module = raw ? new WebApiModule("/api", ResponseSerializer.None(buffered)) : new WebApiModule("/api");
                if (scoped)
                    module.RegisterControllerWithContext(typeof(Controller), _ => new Controller(State), async (_, controller) => { await Task.Yield(); ((IDisposable)controller).Dispose(); });
                else module.RegisterController(() => new Controller(State));
                _server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
                _running = _server.RunAsync(_stop.Token);
                Client = new HttpClient
                {
                    BaseAddress = new Uri(url + "api/"),
                    Timeout = TimeSpan.FromSeconds(10),
                    DefaultRequestVersion = HttpVersion.Version11,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
            }
            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { _server.Dispose(); Client.Dispose(); _stop.Dispose(); }
            }
        }
    }
}
